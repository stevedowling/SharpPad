using SharpPad.Engine;
using SharpPad.Runtime;
using Xunit;

namespace SharpPad.Engine.Tests;

public class DirectiveParsingTests
{
    [Fact]
    public void Parses_nuget_dll_and_connection_directives_and_preserves_line_numbers()
    {
        var doc = ScriptDocument.Parse(
            "#r \"nuget: Newtonsoft.Json, 13.0.3\"\n" +
            "#r \"nuget: Humanizer.Core\"\n" +
            "#r \"/opt/libs/MyLib.dll\"\n" +
            "#connection \"mydb\"\n" +
            "var x = 1;");

        Assert.Equal(2, doc.NugetRefs.Count);
        Assert.Equal(new NugetRef("Newtonsoft.Json", "13.0.3"), doc.NugetRefs[0]);
        Assert.Equal(new NugetRef("Humanizer.Core", null), doc.NugetRefs[1]);
        Assert.Equal("/opt/libs/MyLib.dll", Assert.Single(doc.DllRefs));
        Assert.Equal("mydb", doc.ConnectionName);

        // directive lines blanked, not removed: user code stays on line 5
        var lines = doc.CodeForCompilation.Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Equal(new string(' ', "#r \"nuget: Newtonsoft.Json, 13.0.3\"".Length), lines[0]);
        Assert.Equal("var x = 1;", lines[4]);
    }

    [Fact]
    public void Directive_lines_preserve_character_offsets_for_editor_services()
    {
        const string script =
            "#r \"nuget: Humanizer.Core, 2.14.1\"\n" +
            "var value = Humanizer.InflectorExtensions.Humanize(\"PascalCaseInput\");\n";

        var doc = ScriptDocument.Parse(script);

        Assert.Equal(script.Length, doc.CodeForCompilation.Length);
        Assert.Equal(script.IndexOf("var value", StringComparison.Ordinal),
            doc.CodeForCompilation.IndexOf("var value", StringComparison.Ordinal));
    }
}

public class ExecutorFixture
{
    public string RuntimeDll { get; } = typeof(Protocol).Assembly.Location;
    public Executor Executor { get; } = new();

    public async Task<List<RunEvent>> RunAsync(string script, string key,
        DbConnectionInfo? conn = null, CancellationToken ct = default)
    {
        var events = new List<RunEvent>();
        var doc = ScriptDocument.Parse(script);
        await foreach (var ev in Executor.RunAsync(doc, key, RuntimeDll, conn, ct))
            events.Add(ev);
        return events;
    }
}

public class ExecutorTests : IClassFixture<ExecutorFixture>
{
    private readonly ExecutorFixture _fx;
    public ExecutorTests(ExecutorFixture fx) => _fx = fx;

    [Fact]
    public async Task Linq_query_dumps_as_table_and_console_is_captured()
    {
        var events = await _fx.RunAsync(
            """
            Console.WriteLine("hello console");
            var people = new[]
            {
                new { Name = "Ada", Age = 36 },
                new { Name = "Alan", Age = 41 },
            };
            people.Where(p => p.Age > 30).OrderBy(p => p.Name).Dump("filtered");
            """,
            "test-linq");

        var dump = Assert.Single(events.OfType<DumpEvent>());
        Assert.Equal("filtered", dump.Title);
        Assert.Equal("table", dump.Node.Kind);
        Assert.Equal(new[] { "Name", "Age" }, dump.Node.Columns!.ToArray());
        Assert.Equal(2, dump.Node.Rows!.Count);
        Assert.Equal("Ada", dump.Node.Rows[0][0].Value);

        Assert.Contains(events.OfType<ConsoleEvent>(), e => e.Text == "hello console" && !e.IsError);
        var done = Assert.Single(events.OfType<CompletedEvent>());
        Assert.Equal(0, done.ExitCode);
    }

    [Fact]
    public async Task Object_dump_renders_properties_and_nested_values()
    {
        var events = await _fx.RunAsync(
            """
            new { Name = "box", Size = new { W = 2, H = 3 }, Tags = new[] { "a", "b" } }.Dump();
            """,
            "test-object");

        var node = Assert.Single(events.OfType<DumpEvent>()).Node;
        Assert.Equal("object", node.Kind);
        Assert.Equal(3, node.Props!.Count);
        Assert.Equal("scalar", node.Props[0].Value.Kind);
        Assert.Equal("object", node.Props[1].Value.Kind);
        Assert.Equal("table", node.Props[2].Value.Kind);
    }

    [Fact]
    public async Task Compile_error_maps_to_editor_line()
    {
        var events = await _fx.RunAsync(
            "#r \"nuget: Humanizer.Core, 2.14.1\"\n" +
            "var ok = 1;\n" +
            "var broken = ;\n",
            "test-error");

        Assert.Contains(events, e => e is BuildFailedEvent);
        var diag = events.OfType<DiagnosticEvent>().First(d => d.Severity == "error");
        Assert.Equal(3, diag.Line);
    }

    [Fact]
    public async Task Nuget_package_reference_resolves_and_runs()
    {
        var events = await _fx.RunAsync(
            "#r \"nuget: Humanizer.Core, 2.14.1\"\n" +
            "using Humanizer;\n" +
            "\"PascalCaseInput\".Humanize().Dump();\n",
            "test-nuget");

        var dump = Assert.Single(events.OfType<DumpEvent>());
        Assert.Equal("Pascal case input", dump.Node.Value);
        Assert.Equal(0, Assert.Single(events.OfType<CompletedEvent>()).ExitCode);
    }

    [Fact]
    public async Task Unhandled_exception_reaches_the_ui_as_error()
    {
        var events = await _fx.RunAsync(
            """throw new InvalidOperationException("boom");""",
            "test-exception");

        Assert.True(
            events.OfType<ScriptExceptionEvent>().Any(e => e.Text.Contains("boom")) ||
            events.OfType<ConsoleEvent>().Any(e => e.IsError && e.Text.Contains("boom")),
            "expected the exception text to surface");
        Assert.NotEqual(0, Assert.Single(events.OfType<CompletedEvent>()).ExitCode);
    }

    [Fact]
    public async Task Cancellation_kills_the_child_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = new List<RunEvent>();
        var doc = ScriptDocument.Parse(
            """
            Console.WriteLine("started");
            while (true) Thread.Sleep(100);
            """);

        var run = Task.Run(async () =>
        {
            await foreach (var ev in _fx.Executor.RunAsync(doc, "test-cancel", _fx.RuntimeDll, null, cts.Token))
            {
                events.Add(ev);
                if (ev is ConsoleEvent { Text: "started" }) cts.CancelAfter(TimeSpan.FromMilliseconds(200));
            }
        });

        await run.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(events, e => e is CompletedEvent);
    }

    [Fact]
    public async Task Sqlite_connection_query_dumps_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sharppad-test-{Guid.NewGuid():N}.db");
        try
        {
            var conn = new DbConnectionInfo("testdb", "sqlite", $"Data Source={dbPath}");
            var events = await _fx.RunAsync(
                """
                #connection "testdb"
                Execute("create table people (name text, age int)");
                Execute("insert into people values ('Ada', 36), ('Alan', 41)");
                Query("select name, age from people where age > 30 order by name").Dump("rows");
                """,
                "test-sqlite", conn);

            var dump = Assert.Single(events.OfType<DumpEvent>());
            Assert.Equal("table", dump.Node.Kind);
            Assert.Equal(new[] { "name", "age" }, dump.Node.Columns!.ToArray());
            Assert.Equal(2, dump.Node.Rows!.Count);
            Assert.Equal("Ada", dump.Node.Rows[0][0].Value);
            Assert.Equal(0, Assert.Single(events.OfType<CompletedEvent>()).ExitCode);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }
}
