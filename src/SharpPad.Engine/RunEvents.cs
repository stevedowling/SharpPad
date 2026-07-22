using SharpPad.Runtime;

namespace SharpPad.Engine;

public abstract record RunEvent;

public sealed record DumpEvent(string? Title, DumpNode Node) : RunEvent;
public sealed record ConsoleEvent(string Text, bool IsError) : RunEvent;
public sealed record ScriptExceptionEvent(string Text) : RunEvent;
public sealed record DiagnosticEvent(int Line, int Column, string Severity, string Code, string Message) : RunEvent;
public sealed record BuildFailedEvent(string RawOutput) : RunEvent;
public sealed record CompletedEvent(long? ElapsedMs, int ExitCode) : RunEvent;
