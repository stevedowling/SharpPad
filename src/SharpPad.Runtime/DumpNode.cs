using System.Text.Json.Serialization;

namespace SharpPad.Runtime;

/// <summary>
/// Serializable tree describing a dumped value. Kinds:
/// "scalar", "object", "table", "list", "null", "error".
/// One class rather than a polymorphic hierarchy so both ends
/// deserialize without custom converters.
/// </summary>
public sealed class DumpNode
{
    [JsonPropertyName("k")] public string Kind { get; set; } = "null";
    [JsonPropertyName("t")] public string? TypeName { get; set; }
    [JsonPropertyName("v")] public string? Value { get; set; }

    // object
    [JsonPropertyName("p")] public List<DumpProp>? Props { get; set; }

    // table
    [JsonPropertyName("c")] public List<string>? Columns { get; set; }
    [JsonPropertyName("r")] public List<List<DumpNode>>? Rows { get; set; }

    // list (heterogeneous / no columns)
    [JsonPropertyName("i")] public List<DumpNode>? Items { get; set; }

    [JsonPropertyName("n")] public int? Count { get; set; }
    [JsonPropertyName("tr")] public bool? Truncated { get; set; }

    public static DumpNode Null() => new() { Kind = "null" };
    public static DumpNode Scalar(string? typeName, string value) =>
        new() { Kind = "scalar", TypeName = typeName, Value = value };
    public static DumpNode Error(string message) =>
        new() { Kind = "error", Value = message };
}

public sealed class DumpProp
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("v")] public DumpNode Value { get; set; } = DumpNode.Null();
}

/// <summary>One protocol frame, one JSON line on the child process stdout.</summary>
public sealed class Frame
{
    [JsonPropertyName("t")] public string Type { get; set; } = "";   // dump | out | err | ex | done
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("node")] public DumpNode? Node { get; set; }
    [JsonPropertyName("s")] public string? Text { get; set; }
    [JsonPropertyName("ms")] public long? ElapsedMs { get; set; }
}
