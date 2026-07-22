using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SharpPad.Runtime;

namespace SharpPad.Engine;

/// <summary>
/// Runs a script: generate project -> dotnet build -> run the built exe as a
/// child process, streaming protocol frames back as RunEvents.
/// Cancellation kills the child process tree.
/// </summary>
public sealed class Executor
{
    public string DotnetPath { get; init; } =
        Environment.GetEnvironmentVariable("SHARPPAD_DOTNET") ?? "dotnet";

    private static readonly Regex DiagRx = new(
        @"^(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<msg>.*?)(\s+\[.*\])?$",
        RegexOptions.Compiled);

    public async IAsyncEnumerable<RunEvent> RunAsync(
        ScriptDocument doc,
        string scriptKey,
        string runtimeDllPath,
        DbConnectionInfo? connection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var dir = ProjectGenerator.BuildDirFor(scriptKey);
        ProjectGenerator.Generate(dir, doc, runtimeDllPath, connection);

        // ---- build ----
        var (buildExit, buildOutput) = await RunProcessCollectAsync(
            DotnetPath, $"build script.csproj -nologo -v:q -clp:NoSummary", dir, ct);

        if (ct.IsCancellationRequested) yield break;

        if (buildExit != 0)
        {
            var seen = new HashSet<string>();
            foreach (var line in buildOutput.Split('\n'))
            {
                var m = DiagRx.Match(line.Trim());
                if (!m.Success || !seen.Add(m.Value)) continue;
                yield return new DiagnosticEvent(
                    int.Parse(m.Groups["line"].Value),
                    int.Parse(m.Groups["col"].Value),
                    m.Groups["sev"].Value,
                    m.Groups["code"].Value,
                    m.Groups["msg"].Value);
            }
            yield return new BuildFailedEvent(buildOutput);
            yield break;
        }

        // ---- run ----
        var dll = Path.Combine(dir, "bin", "Debug", "script.dll");
        var channel = Channel.CreateUnbounded<RunEvent>();
        long? elapsed = null;

        var psi = new ProcessStartInfo(DotnetPath, $"\"{dll}\"")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = e.Data;
            if (line.StartsWith(Protocol.Magic, StringComparison.Ordinal))
            {
                try
                {
                    var frame = JsonSerializer.Deserialize<Frame>(line.AsSpan(Protocol.Magic.Length));
                    switch (frame?.Type)
                    {
                        case "dump" when frame.Node is not null:
                            channel.Writer.TryWrite(new DumpEvent(frame.Title, frame.Node));
                            return;
                        case "out":
                            channel.Writer.TryWrite(new ConsoleEvent(frame.Text ?? "", false));
                            return;
                        case "err":
                            channel.Writer.TryWrite(new ConsoleEvent(frame.Text ?? "", true));
                            return;
                        case "ex":
                            channel.Writer.TryWrite(new ScriptExceptionEvent(frame.Text ?? "unknown error"));
                            return;
                        case "done":
                            elapsed = frame.ElapsedMs;
                            return;
                    }
                }
                catch (JsonException)
                {
                    // fall through: treat as plain output
                }
            }
            channel.Writer.TryWrite(new ConsoleEvent(line, false));
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) channel.Writer.TryWrite(new ConsoleEvent(e.Data, true));
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        });

        _ = Task.Run(async () =>
        {
            await proc.WaitForExitAsync(CancellationToken.None);
            // Parameterless WaitForExit (unlike WaitForExitAsync) blocks until the
            // redirected output streams hit EOF, so no frames are dropped.
            proc.WaitForExit();
            channel.Writer.TryComplete();
        });

        await foreach (var ev in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return ev;

        yield return new CompletedEvent(elapsed, proc.ExitCode);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessCollectAsync(
        string fileName, string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi)!;
        using var reg = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        });
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(CancellationToken.None);
        return (proc.ExitCode, await stdout + "\n" + await stderr);
    }
}
