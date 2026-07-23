using Avalonia;
using SharpPad.Engine;

namespace SharpPad.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CompletionBootstrap.RegisterMsBuild();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
