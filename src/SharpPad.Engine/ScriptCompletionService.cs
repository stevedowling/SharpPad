using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace SharpPad.Engine;

public enum ScriptCompletionTriggerKind
{
    Invoke,
    TypeChar
}

public sealed class ScriptCompletionSet
{
    internal ScriptCompletionSet(
        Document document,
        CompletionService completionService,
        ImmutableArray<ScriptCompletionItem> items,
        int startOffset,
        int endOffset)
    {
        Document = document;
        CompletionService = completionService;
        Items = items;
        StartOffset = startOffset;
        EndOffset = endOffset;
    }

    internal Document Document { get; }
    internal CompletionService CompletionService { get; }
    public ImmutableArray<ScriptCompletionItem> Items { get; }
    public int StartOffset { get; }
    public int EndOffset { get; }
}

public sealed class ScriptCompletionItem
{
    internal ScriptCompletionItem(CompletionItem item, string text)
    {
        Item = item;
        Text = text;
    }

    internal CompletionItem Item { get; }
    public string Text { get; }
}

public sealed record ScriptCompletionChange(
    ImmutableArray<TextChange> TextChanges,
    int? NewPosition);

public sealed class ScriptCompletionService : IAsyncDisposable
{
    public string DotnetPath { get; init; } =
        Environment.GetEnvironmentVariable("SHARPPAD_DOTNET") ?? "dotnet";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Project? _project;
    private DocumentId? _programDocumentId;
    private CompletionProjectState? _state;

    public async Task<ScriptCompletionSet?> GetCompletionsAsync(
        string scriptText,
        string scriptKey,
        string runtimeDllPath,
        DbConnectionInfo? connection,
        int position,
        ScriptCompletionTriggerKind triggerKind,
        char? triggerCharacter = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, scriptText.Length);

        var doc = ScriptDocument.Parse(scriptText);
        var state = CompletionProjectState.Create(scriptKey, runtimeDllPath, connection, doc);

        await _gate.WaitAsync(ct);
        try
        {
            var project = await EnsureProjectAsync(state, doc, runtimeDllPath, connection, ct);
            var diskDocument = project.GetDocument(_programDocumentId!)
                ?? throw new InvalidOperationException("Generated Program.cs was not found.");

            var liveDocument = diskDocument.WithText(SourceText.From(doc.CodeForCompilation));
            var completionService = CompletionService.GetService(liveDocument);
            if (completionService is null)
                return null;

            var completionList = await completionService.GetCompletionsAsync(
                liveDocument,
                position,
                GetTrigger(triggerKind, triggerCharacter),
                cancellationToken: ct);

            if (completionList is null || completionList.ItemsList.Count == 0)
                return null;

            return new ScriptCompletionSet(
                liveDocument,
                completionService,
                completionList.ItemsList.Select(i => new ScriptCompletionItem(i, i.DisplayText)).ToImmutableArray(),
                completionList.Span.Start,
                position);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScriptCompletionChange> GetChangeAsync(
        ScriptCompletionSet completionSet,
        ScriptCompletionItem item,
        CancellationToken ct = default)
    {
        var change = await completionSet.CompletionService.GetChangeAsync(
            completionSet.Document,
            item.Item,
            cancellationToken: ct);

        return new ScriptCompletionChange(change.TextChanges.ToImmutableArray(), change.NewPosition);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DisposeWorkspace();
            _gate.Dispose();
        }
        finally
        {
            // no release: semaphore disposed
        }
    }

    private async Task<Project> EnsureProjectAsync(
        CompletionProjectState state,
        ScriptDocument doc,
        string runtimeDllPath,
        DbConnectionInfo? connection,
        CancellationToken ct)
    {
        if (_project is not null && _programDocumentId is not null && Equals(_state, state))
            return _project;

        DisposeWorkspace();

        var buildDir = ProjectGenerator.BuildDirFor(state.ScriptKey);
        ProjectGenerator.Generate(buildDir, doc, runtimeDllPath, connection);
        await RunProcessAsync(DotnetPath, "restore script.csproj -nologo -v:q", buildDir, ct);

        _workspace = MSBuildWorkspace.Create();

        _project = await _workspace.OpenProjectAsync(Path.Combine(buildDir, "script.csproj"), cancellationToken: ct);
        _programDocumentId = _project.Documents
            .SingleOrDefault(d => string.Equals(Path.GetFileName(d.FilePath), "Program.cs", StringComparison.OrdinalIgnoreCase))
            ?.Id
            ?? throw new InvalidOperationException("Generated Program.cs was not found.");
        _state = state;
        return _project;
    }

    private void DisposeWorkspace()
    {
        _programDocumentId = null;
        _project = null;
        _state = null;
        _workspace?.Dispose();
        _workspace = null;
    }

    private static CompletionTrigger GetTrigger(ScriptCompletionTriggerKind triggerKind, char? triggerCharacter) =>
        triggerKind switch
        {
            ScriptCompletionTriggerKind.Invoke => CompletionTrigger.Invoke,
            ScriptCompletionTriggerKind.TypeChar when triggerCharacter is char c => CompletionTrigger.CreateInsertionTrigger(c),
            ScriptCompletionTriggerKind.TypeChar => throw new ArgumentNullException(nameof(triggerCharacter)),
            _ => throw new ArgumentOutOfRangeException(nameof(triggerKind))
        };

    private static async Task RunProcessAsync(
        string fileName,
        string args,
        string workingDir,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        using var reg = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        });

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(CancellationToken.None);
        var output = (await stdout) + "\n" + (await stderr);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("dotnet restore failed:\n" + output.Trim());
    }

    private sealed record CompletionProjectState(
        string ScriptKey,
        string RuntimeDllPath,
        string? ConnectionProvider,
        string? ConnectionString,
        ImmutableArray<NugetRef> NugetRefs,
        ImmutableArray<string> DllRefs)
    {
        public static CompletionProjectState Create(
            string scriptKey,
            string runtimeDllPath,
            DbConnectionInfo? connection,
            ScriptDocument doc) =>
            new(
                scriptKey,
                runtimeDllPath,
                connection?.Provider,
                connection?.ConnectionString,
                doc.NugetRefs.ToImmutableArray(),
                doc.DllRefs.ToImmutableArray());
    }
}
