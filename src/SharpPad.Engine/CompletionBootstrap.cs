using Microsoft.Build.Locator;

namespace SharpPad.Engine;

public static class CompletionBootstrap
{
    public static void RegisterMsBuild()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }
}
