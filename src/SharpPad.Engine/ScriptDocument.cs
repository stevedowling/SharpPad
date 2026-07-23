using System.Text.RegularExpressions;

namespace SharpPad.Engine;

public sealed record NugetRef(string Package, string? Version);

/// <summary>
/// A script plus its parsed directives. Directives sit anywhere in the file on
/// their own line and are blanked out (not removed) so compiler line numbers
/// match the editor exactly:
///   #r "nuget: Newtonsoft.Json, 13.0.3"
///   #r "nuget: Humanizer"
///   #r "/path/to/MyLib.dll"
///   #connection "mydb"
/// </summary>
public sealed class ScriptDocument
{
    public string OriginalText { get; private init; } = "";
    public string CodeForCompilation { get; private init; } = "";
    public IReadOnlyList<NugetRef> NugetRefs { get; private init; } = [];
    public IReadOnlyList<string> DllRefs { get; private init; } = [];
    public string? ConnectionName { get; private init; }

    private static readonly Regex NugetRx = new(
        """^\s*#r\s+"nuget:\s*(?<pkg>[A-Za-z0-9_.-]+)\s*(?:,\s*(?<ver>[A-Za-z0-9_.+-]+)\s*)?"\s*$""",
        RegexOptions.Compiled);
    private static readonly Regex DllRx = new(
        """^\s*#r\s+"(?<path>[^"]+\.dll)"\s*$""",
        RegexOptions.Compiled);
    private static readonly Regex ConnRx = new(
        """^\s*#connection\s+"(?<name>[^"]+)"\s*$""",
        RegexOptions.Compiled);

    public static ScriptDocument Parse(string text)
    {
        var nugets = new List<NugetRef>();
        var dlls = new List<string>();
        string? connection = null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            Match m;
            if ((m = NugetRx.Match(line)).Success)
            {
                nugets.Add(new NugetRef(m.Groups["pkg"].Value,
                    m.Groups["ver"].Success ? m.Groups["ver"].Value : null));
                lines[i] = new string(' ', line.Length);
            }
            else if ((m = DllRx.Match(line)).Success)
            {
                var path = m.Groups["path"].Value;
                if (path.StartsWith("~/"))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
                dlls.Add(path);
                lines[i] = new string(' ', line.Length);
            }
            else if ((m = ConnRx.Match(line)).Success)
            {
                connection = m.Groups["name"].Value;
                lines[i] = new string(' ', line.Length);
            }
        }

        return new ScriptDocument
        {
            OriginalText = text,
            CodeForCompilation = string.Join("\n", lines),
            NugetRefs = nugets,
            DllRefs = dlls,
            ConnectionName = connection
        };
    }
}
