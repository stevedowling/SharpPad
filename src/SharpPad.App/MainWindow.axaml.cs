using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using SharpPad.Engine;
using TextMateSharp.Grammars;

namespace SharpPad.App;

public partial class MainWindow : Window
{
    private readonly TextEditor _editor;
    private readonly Executor _executor = new();
    private readonly ScriptCompletionService _completionService = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _completionCts;
    private CompletionWindow? _completionWindow;
    private string? _currentPath;
    private List<DbConnectionInfo> _connections = [];

    private const string SampleScript =
        """
        // SharpPad — LINQPad-style scratchpad for Linux
        //   #r "nuget: Newtonsoft.Json, 13.0.3"   NuGet reference (version optional)
        //   #r "/path/to/Your.dll"                DLL reference
        //   #connection "mydb"                    database (define via Manage…)
        // Write top-level statements; classes can follow below. F5 to run.

        var primes = Enumerable.Range(2, 60)
            .Where(n => Enumerable.Range(2, Math.Max(0, (int)Math.Sqrt(n) - 1)).All(d => n % d != 0));
        primes.Dump("primes");

        new { Now = DateTime.Now, Host = Environment.MachineName, Pid = Environment.ProcessId }.Dump();
        """;

    public MainWindow()
    {
        InitializeComponent();

        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Code,JetBrains Mono,DejaVu Sans Mono,monospace"),
            FontSize = 14,
            Background = Brush.Parse("#1E1E1E")
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        EditorHost.Child = _editor;

        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        var textMate = _editor.InstallTextMate(registryOptions);
        textMate.SetGrammar(registryOptions.GetScopeByLanguageId(
            registryOptions.GetLanguageByExtension(".cs").Id));

        _editor.Text = SampleScript;
        _editor.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text == ".")
                _ = ShowCompletionsAsync(ScriptCompletionTriggerKind.TypeChar, '.');
        };

        RunButton.Click += (_, _) => _ = RunAsync();
        StopButton.Click += (_, _) => _cts?.Cancel();
        NewButton.Click += (_, _) => NewScript();
        SaveButton.Click += (_, _) => _ = SaveAsync();
        SaveAsButton.Click += (_, _) => _ = SaveAsAsync();
        ManageConnectionsButton.Click += (_, _) => _ = ManageConnectionsAsync();
        ScriptsList.SelectionChanged += (_, _) => LoadSelectedScript();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { e.Handled = true; _ = RunAsync(); }
            else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; _ = SaveAsync(); }
            else if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                _ = ShowCompletionsAsync(ScriptCompletionTriggerKind.Invoke);
            }
        };

        Closed += async (_, _) =>
        {
            _completionCts?.Cancel();
            await _completionService.DisposeAsync();
        };

        RefreshScriptsList();
        RefreshConnections();
    }

    // ---------- running ----------

    private async Task RunAsync()
    {
        if (_cts is not null) return;

        var doc = ScriptDocument.Parse(_editor.Text ?? "");

        var conn = ResolveActiveConnection(doc);
        if (doc.ConnectionName is not null && conn is null)
        {
            StatusText.Text = $"Unknown connection '{doc.ConnectionName}' — define it via Manage…";
            return;
        }

        ResultsPanel.Children.Clear();
        _cts = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = "Building…";

        var runtimeDll = Path.Combine(AppContext.BaseDirectory, "SharpPad.Runtime.dll");
        var key = _currentPath ?? "untitled";
        bool running = false;

        try
        {
            await foreach (var ev in _executor.RunAsync(doc, key, runtimeDll, conn, _cts.Token))
            {
                if (!running && ev is not DiagnosticEvent and not BuildFailedEvent)
                {
                    running = true;
                    StatusText.Text = "Running…";
                }
                Append(ev);
            }
        }
        catch (Exception ex)
        {
            AppendText("Runner error: " + ex.Message, isError: true);
            StatusText.Text = "Failed";
        }
        finally
        {
            var cancelled = _cts.IsCancellationRequested;
            _cts.Dispose();
            _cts = null;
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            if (cancelled) StatusText.Text = "Cancelled";
        }
    }

    private void Append(RunEvent ev)
    {
        switch (ev)
        {
            case DumpEvent d:
                ResultsPanel.Children.Add(DumpNodeControl.BuildBlock(d.Title, d.Node));
                break;

            case ConsoleEvent c:
                AppendText(c.Text, c.IsError);
                break;

            case ScriptExceptionEvent x:
                AppendText(x.Text, isError: true);
                break;

            case DiagnosticEvent diag:
                var tb = new TextBlock
                {
                    Text = $"{diag.Severity} {diag.Code} (line {diag.Line}, col {diag.Column}): {diag.Message}",
                    Foreground = diag.Severity == "error" ? Brush.Parse("#F48771") : Brush.Parse("#DCDCAA"),
                    FontFamily = new FontFamily("monospace"),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    TextWrapping = TextWrapping.Wrap
                };
                var line = diag.Line;
                var col = diag.Column;
                tb.PointerPressed += (_, _) =>
                {
                    _editor.TextArea.Caret.Line = line;
                    _editor.TextArea.Caret.Column = col;
                    _editor.TextArea.Caret.BringCaretToView();
                    _editor.Focus();
                };
                ResultsPanel.Children.Add(tb);
                break;

            case BuildFailedEvent bf:
                ResultsPanel.Children.Add(new Expander
                {
                    Header = "Build failed — full output",
                    Content = new TextBlock
                    {
                        Text = bf.RawOutput.Trim(),
                        FontFamily = new FontFamily("monospace"),
                        FontSize = 12,
                        Foreground = Brush.Parse("#AAAAAA"),
                        TextWrapping = TextWrapping.Wrap
                    }
                });
                StatusText.Text = "Build failed";
                break;

            case CompletedEvent done:
                StatusText.Text = done.ElapsedMs is long ms
                    ? $"Done in {ms} ms (exit {done.ExitCode})"
                    : $"Exited with code {done.ExitCode}";
                break;
        }
        ResultsScroll.ScrollToEnd();
    }

    private void AppendText(string text, bool isError)
    {
        ResultsPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = isError ? Brush.Parse("#F48771") : Brush.Parse("#CCCCCC"),
            FontFamily = new FontFamily("Cascadia Code,JetBrains Mono,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
    }

    private async Task ShowCompletionsAsync(ScriptCompletionTriggerKind triggerKind, char? triggerCharacter = null)
    {
        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _completionCts = new CancellationTokenSource();
        var ct = _completionCts.Token;

        try
        {
            var text = _editor.Text ?? "";
            var doc = ScriptDocument.Parse(text);
            var conn = ResolveActiveConnection(doc);
            if (doc.ConnectionName is not null && conn is null)
                return;

            var completionSet = await _completionService.GetCompletionsAsync(
                text,
                _currentPath ?? "untitled",
                Path.Combine(AppContext.BaseDirectory, "SharpPad.Runtime.dll"),
                conn,
                _editor.CaretOffset,
                triggerKind,
                triggerCharacter,
                ct);

            if (ct.IsCancellationRequested || completionSet is null || completionSet.Items.Length == 0)
                return;

            _completionWindow?.Close();
            var window = new CompletionWindow(_editor.TextArea)
            {
                StartOffset = completionSet.StartOffset,
                EndOffset = completionSet.EndOffset,
                CloseWhenCaretAtBeginning = true
            };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_completionWindow, window))
                    _completionWindow = null;
            };

            foreach (var item in completionSet.Items)
                window.CompletionList.CompletionData.Add(new RoslynCompletionData(_completionService, completionSet, item));

            _completionWindow = window;
            window.Show();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "IntelliSense unavailable: " + ex.Message;
        }
    }

    // ---------- scripts ----------

    private void NewScript()
    {
        _completionWindow?.Close();
        _currentPath = null;
        _editor.Text = SampleScript;
        ScriptNameText.Text = "untitled";
        ScriptsList.SelectedItem = null;
    }

    private async Task SaveAsync()
    {
        if (_currentPath is null && !await TrySelectSavePathAsync("Save script"))
            return;

        await File.WriteAllTextAsync(_currentPath!, _editor.Text ?? "");
        ScriptNameText.Text = Path.GetFileName(_currentPath);
        StatusText.Text = $"Saved {Path.GetFileName(_currentPath)}";
        RefreshScriptsList();
    }

    private async Task SaveAsAsync()
    {
        if (!await TrySelectSavePathAsync("Save script as (rename/fork)"))
            return;

        await SaveAsync();
    }

    private async Task<bool> TrySelectSavePathAsync(string title)
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(Workspace.ScriptsDir);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedStartLocation = start,
            DefaultExtension = "cs",
            FileTypeChoices = [new FilePickerFileType("C# script") { Patterns = ["*.cs"] }]
        });
        if (file is null) return false;

        _currentPath = file.Path.LocalPath;
        return true;
    }

    private void RefreshScriptsList()
    {
        var selected = _currentPath;
        ScriptsList.Items.Clear();
        foreach (var path in Workspace.ListScripts())
        {
            var item = new ListBoxItem
            {
                Content = Path.GetRelativePath(Workspace.ScriptsDir, path),
                Tag = path
            };
            ScriptsList.Items.Add(item);
            if (path == selected) ScriptsList.SelectedItem = item;
        }
    }

    private void LoadSelectedScript()
    {
        if (ScriptsList.SelectedItem is not ListBoxItem { Tag: string path } || path == _currentPath)
            return;
        try
        {
            _completionWindow?.Close();
            _editor.Text = File.ReadAllText(path);
            _currentPath = path;
            ScriptNameText.Text = Path.GetFileName(path);
            StatusText.Text = "Loaded " + Path.GetFileName(path);
        }
        catch (IOException ex)
        {
            StatusText.Text = "Load failed: " + ex.Message;
        }
    }

    // ---------- connections ----------

    private void RefreshConnections()
    {
        _connections = Workspace.LoadConnections();
        var previous = ConnectionCombo.SelectedIndex;
        ConnectionCombo.Items.Clear();
        ConnectionCombo.Items.Add("(none)");
        foreach (var c in _connections)
            ConnectionCombo.Items.Add($"{c.Name} ({c.Provider})");
        ConnectionCombo.SelectedIndex = previous >= 0 && previous < ConnectionCombo.Items.Count ? previous : 0;
    }

    private async Task ManageConnectionsAsync()
    {
        await new ConnectionsWindow().ShowDialog(this);
        RefreshConnections();
    }

    private DbConnectionInfo? ResolveActiveConnection(ScriptDocument doc)
    {
        if (doc.ConnectionName is not null)
            return _connections.FirstOrDefault(c => c.Name == doc.ConnectionName);

        return ConnectionCombo.SelectedIndex > 0
            ? _connections[ConnectionCombo.SelectedIndex - 1]
            : null;
    }

    private sealed class RoslynCompletionData(
        ScriptCompletionService completionService,
        ScriptCompletionSet completionSet,
        ScriptCompletionItem item) : ICompletionData
    {
        public string Text => item.Text;
        public object Content => item.Text;
        public object? Description => null;
        public double Priority => 0;
        public Avalonia.Media.IImage? Image => null;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var change = completionService.GetChangeAsync(completionSet, item).GetAwaiter().GetResult();
            foreach (var textChange in change.TextChanges.OrderByDescending(c => c.Span.Start))
                textArea.Document.Replace(textChange.Span.Start, textChange.Span.Length, textChange.NewText);

            if (change.NewPosition is int newPosition)
                textArea.Caret.Offset = newPosition;
        }
    }
}
