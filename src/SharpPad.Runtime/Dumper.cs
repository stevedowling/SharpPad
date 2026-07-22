using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpPad.Runtime;

public static class DumpExtensions
{
    /// <summary>LINQPad-style Dump. Returns the value for chaining.</summary>
    public static T Dump<T>(this T value, string? title = null)
    {
        var node = Dumper.Build(value);
        Protocol.SendDump(title, node);
        return value;
    }
}

public static class Dumper
{
    public static int MaxDepth { get; set; } = 5;
    public static int MaxItems { get; set; } = 1000;
    public static int MaxStringLength { get; set; } = 8000;

    public static DumpNode Build(object? value) =>
        Build(value, MaxDepth, new RefStack());

    private sealed class RefStack
    {
        private readonly HashSet<object> _set = new(ReferenceEqualityComparer.Instance);
        public bool Push(object o) => _set.Add(o);
        public void Pop(object o) => _set.Remove(o);
    }

    private static DumpNode Build(object? value, int depth, RefStack stack)
    {
        if (value is null) return DumpNode.Null();

        var type = value.GetType();

        if (IsSimple(type)) return DumpNode.Scalar(FriendlyName(type), FormatSimple(value));

        if (value is byte[] bytes)
            return DumpNode.Scalar("byte[]", $"byte[{bytes.Length}]");

        if (depth <= 0)
            return DumpNode.Scalar(FriendlyName(type), SafeToString(value));

        if (!type.IsValueType)
        {
            if (!stack.Push(value))
                return DumpNode.Scalar(FriendlyName(type), "(cycle)");
        }

        try
        {
            if (value is IDictionary dict) return BuildDictionary(dict, depth, stack);
            if (value is IEnumerable seq) return BuildSequence(seq, type, depth, stack);
            return BuildObject(value, type, depth, stack);
        }
        finally
        {
            if (!type.IsValueType) stack.Pop(value);
        }
    }

    private static DumpNode BuildDictionary(IDictionary dict, int depth, RefStack stack)
    {
        var node = new DumpNode
        {
            Kind = "table",
            TypeName = FriendlyName(dict.GetType()),
            Columns = new List<string> { "Key", "Value" },
            Rows = new List<List<DumpNode>>()
        };
        int i = 0;
        foreach (DictionaryEntry e in dict)
        {
            if (i++ >= MaxItems) { node.Truncated = true; break; }
            node.Rows.Add(new List<DumpNode>
            {
                Build(e.Key, depth - 1, stack),
                Build(e.Value, depth - 1, stack)
            });
        }
        node.Count = node.Rows.Count;
        return node;
    }

    private static DumpNode BuildSequence(IEnumerable seq, Type type, int depth, RefStack stack)
    {
        var items = new List<object?>();
        bool truncated = false;
        foreach (var item in seq)
        {
            if (items.Count >= MaxItems) { truncated = true; break; }
            items.Add(item);
        }

        // Rows that are string-keyed dictionaries (e.g. Dapper dynamic rows)
        // render as a table with the keys as columns.
        if (items.Count > 0 && items.All(x => x is IDictionary<string, object?>))
        {
            var cols = new List<string>();
            foreach (var item in items)
                foreach (var key in DictKeys(item!))
                    if (!cols.Contains(key)) cols.Add(key);

            var rows = items.Select(item =>
                cols.Select(c => Build(DictGet(item!, c), depth - 1, stack)).ToList()).ToList();

            return new DumpNode
            {
                Kind = "table", TypeName = FriendlyName(type),
                Columns = cols, Rows = rows, Count = rows.Count,
                Truncated = truncated ? true : null
            };
        }

        // All simple values: single-column table.
        if (items.All(x => x is null || IsSimple(x.GetType())))
        {
            return new DumpNode
            {
                Kind = "table", TypeName = FriendlyName(type),
                Columns = new List<string> { "Value" },
                Rows = items.Select(x => new List<DumpNode> { Build(x, depth - 1, stack) }).ToList(),
                Count = items.Count,
                Truncated = truncated ? true : null
            };
        }

        // Uniform complex type: columns from its members.
        var elemTypes = items.Where(x => x is not null).Select(x => x!.GetType()).Distinct().ToList();
        if (elemTypes.Count == 1 && GetMembers(elemTypes[0]).Count > 0)
        {
            var members = GetMembers(elemTypes[0]);
            return new DumpNode
            {
                Kind = "table", TypeName = FriendlyName(type),
                Columns = members.Select(m => m.Name).ToList(),
                Rows = items.Select(item => members.Select(m =>
                {
                    if (item is null) return DumpNode.Null();
                    var v = GetValueSafe(m, item, out var err);
                    return err is not null ? DumpNode.Error(err) : Build(v, depth - 1, stack);
                }).ToList()).ToList(),
                Count = items.Count,
                Truncated = truncated ? true : null
            };
        }

        // Heterogeneous: plain list.
        return new DumpNode
        {
            Kind = "list", TypeName = FriendlyName(type),
            Items = items.Select(x => Build(x, depth - 1, stack)).ToList(),
            Count = items.Count,
            Truncated = truncated ? true : null
        };
    }

    private static DumpNode BuildObject(object value, Type type, int depth, RefStack stack)
    {
        var members = GetMembers(type);
        if (members.Count == 0)
            return DumpNode.Scalar(FriendlyName(type), SafeToString(value));

        var node = new DumpNode
        {
            Kind = "object", TypeName = FriendlyName(type),
            Props = new List<DumpProp>()
        };
        foreach (var m in members)
        {
            var v = GetValueSafe(m, value, out var err);
            node.Props.Add(new DumpProp
            {
                Name = m.Name,
                Value = err is not null ? DumpNode.Error(err) : Build(v, depth - 1, stack)
            });
        }
        return node;
    }

    // ---- reflection helpers ----

    private static readonly Dictionary<Type, List<MemberInfo>> MemberCache = new();

    private static List<MemberInfo> GetMembers(Type type)
    {
        lock (MemberCache)
        {
            if (MemberCache.TryGetValue(type, out var cached)) return cached;
            var list = new List<MemberInfo>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.GetIndexParameters().Length == 0) list.Add(p);
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                list.Add(f);
            MemberCache[type] = list;
            return list;
        }
    }

    private static object? GetValueSafe(MemberInfo m, object target, out string? error)
    {
        error = null;
        try
        {
            return m switch
            {
                PropertyInfo p => p.GetValue(target),
                FieldInfo f => f.GetValue(target),
                _ => null
            };
        }
        catch (Exception ex)
        {
            error = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message;
            return null;
        }
    }

    private static IEnumerable<string> DictKeys(object dict) => dict switch
    {
        IDictionary<string, object?> d => d.Keys,
        _ => Enumerable.Empty<string>()
    };

    private static object? DictGet(object dict, string key) => dict switch
    {
        IDictionary<string, object?> d => d.TryGetValue(key, out var v) ? v : null,
        _ => null
    };

    private static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum
            || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset)
            || t == typeof(DateOnly) || t == typeof(TimeOnly)
            || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(Uri);
    }

    private static string FormatSimple(object value)
    {
        var s = value switch
        {
            string str => str,
            double d => d.ToString("R"),
            float f => f.ToString("R"),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            _ => value.ToString() ?? ""
        };
        return s.Length > MaxStringLength ? s[..MaxStringLength] + "…" : s;
    }

    private static string SafeToString(object value)
    {
        try { var s = value.ToString() ?? ""; return s.Length > MaxStringLength ? s[..MaxStringLength] + "…" : s; }
        catch (Exception ex) { return "(ToString threw " + ex.GetType().Name + ")"; }
    }

    public static string FriendlyName(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            var args = string.Join(", ", t.GetGenericArguments().Select(FriendlyName));
            return $"{name}<{args}>";
        }
        if (t.IsArray) return FriendlyName(t.GetElementType()!) + "[]";
        return t.Name switch
        {
            "Int32" => "int", "Int64" => "long", "String" => "string",
            "Boolean" => "bool", "Double" => "double", "Single" => "float",
            "Decimal" => "decimal", "Object" => "object", "Byte" => "byte",
            _ => t.Name
        };
    }
}
