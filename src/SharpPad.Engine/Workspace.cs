using System.Text.Json;

namespace SharpPad.Engine;

public sealed record DbConnectionInfo(string Name, string Provider, string ConnectionString);

/// <summary>
/// Filesystem layout under SHARPPAD_HOME (default ~/.sharppad):
///   scripts/       saved scripts (*.cs)
///   build/         per-script generated projects (cached for incremental builds)
///   connections.json
/// </summary>
public static class Workspace
{
    public static string Root =>
        Environment.GetEnvironmentVariable("SHARPPAD_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharppad");

    public static string ScriptsDir => Ensure(Path.Combine(Root, "scripts"));
    public static string BuildDir => Ensure(Path.Combine(Root, "build"));
    private static string ConnectionsFile => Path.Combine(Ensure(Root), "connections.json");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<string> ListScripts() =>
        Directory.EnumerateFiles(ScriptsDir, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static List<DbConnectionInfo> LoadConnections()
    {
        if (!File.Exists(ConnectionsFile)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DbConnectionInfo>>(File.ReadAllText(ConnectionsFile)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveConnections(IEnumerable<DbConnectionInfo> connections) =>
        File.WriteAllText(ConnectionsFile,
            JsonSerializer.Serialize(connections.ToList(), new JsonSerializerOptions { WriteIndented = true }));
}
