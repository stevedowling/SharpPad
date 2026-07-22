using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using SharpPad.App;
using SharpPad.Runtime;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(SharpPad.App.Tests.TestAppBuilder))]

namespace SharpPad.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class UiSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_constructs_and_shows()
    {
        var window = new MainWindow();
        window.Show();
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ConnectionsWindow_constructs_and_shows()
    {
        var window = new ConnectionsWindow();
        window.Show();
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void DumpNodeControl_renders_nested_structures()
    {
        var node = new DumpNode
        {
            Kind = "table",
            TypeName = "List<Person>",
            Columns = ["Name", "Address"],
            Count = 1,
            Rows =
            [
                [
                    DumpNode.Scalar("string", "Ada"),
                    new DumpNode
                    {
                        Kind = "object",
                        TypeName = "Address",
                        Props =
                        [
                            new DumpProp { Name = "City", Value = DumpNode.Scalar("string", "Wellard") },
                            new DumpProp { Name = "Tags", Value = new DumpNode
                                {
                                    Kind = "list",
                                    Items = [DumpNode.Null(), DumpNode.Error("oops")]
                                }
                            }
                        ]
                    }
                ]
            ]
        };

        var control = DumpNodeControl.BuildBlock("people", node);
        Assert.NotNull(control);
    }
}
