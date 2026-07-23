using SharpPad.Engine;
using SharpPad.Runtime;
using Xunit;

namespace SharpPad.Engine.Tests;

public class CompletionTests
{
    private static string RuntimeDll => typeof(Protocol).Assembly.Location;

    [Fact]
    public async Task Completion_returns_framework_members()
    {
        CompletionBootstrap.RegisterMsBuild();
        await using var service = new ScriptCompletionService();

        const string script = "Enumerable.";
        var completions = await service.GetCompletionsAsync(
            script,
            $"completion-framework-{Guid.NewGuid():N}",
            RuntimeDll,
            null,
            script.Length,
            ScriptCompletionTriggerKind.TypeChar,
            '.');

        Assert.NotNull(completions);
        Assert.Contains(completions!.Items, item => item.Text == "Range");
    }

    [Fact]
    public async Task Completion_returns_runtime_extensions()
    {
        CompletionBootstrap.RegisterMsBuild();
        await using var service = new ScriptCompletionService();

        const string script = "new[] { 1, 2, 3 }.";
        var completions = await service.GetCompletionsAsync(
            script,
            $"completion-runtime-{Guid.NewGuid():N}",
            RuntimeDll,
            null,
            script.Length,
            ScriptCompletionTriggerKind.TypeChar,
            '.');

        Assert.NotNull(completions);
        Assert.Contains(completions!.Items, item => item.Text == "Dump");
    }

    [Fact]
    public async Task Completion_returns_nuget_extension_members()
    {
        CompletionBootstrap.RegisterMsBuild();
        await using var service = new ScriptCompletionService();

        const string script =
            """
            #r "nuget: Humanizer.Core, 2.14.1"
            using Humanizer;
            "PascalCaseInput".
            """;

        var completions = await service.GetCompletionsAsync(
            script,
            $"completion-nuget-{Guid.NewGuid():N}",
            RuntimeDll,
            null,
            script.Length,
            ScriptCompletionTriggerKind.TypeChar,
            '.');

        Assert.NotNull(completions);
        Assert.Contains(completions!.Items, item => item.Text == "Humanize");
    }
}
