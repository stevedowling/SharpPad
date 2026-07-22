using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SharpPad.Runtime;

namespace SharpPad.App;

/// <summary>Renders a DumpNode tree as Avalonia controls, LINQPad-style.</summary>
public static class DumpNodeControl
{
    private static readonly IBrush ValueBrush = Brush.Parse("#DCDCDC");
    private static readonly IBrush MutedBrush = Brush.Parse("#808080");
    private static readonly IBrush TypeBrush = Brush.Parse("#569CD6");
    private static readonly IBrush NameBrush = Brush.Parse("#9CDCFE");
    private static readonly IBrush ErrorBrush = Brush.Parse("#F48771");
    private static readonly IBrush BorderBrush = Brush.Parse("#3F3F46");
    private static readonly IBrush HeaderBg = Brush.Parse("#2D2D30");
    private static readonly IBrush AltRowBg = Brush.Parse("#242426");
    private static readonly FontFamily Mono = new("Cascadia Code,JetBrains Mono,DejaVu Sans Mono,monospace");

    /// <summary>Top-level block for one Dump() call.</summary>
    public static Control BuildBlock(string? title, DumpNode node)
    {
        var panel = new StackPanel { Spacing = 3 };
        if (!string.IsNullOrEmpty(title))
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush.Parse("#4EC9B0"),
                FontWeight = FontWeight.Bold,
                FontSize = 13
            });
        panel.Children.Add(Build(node, depth: 0));
        return new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8),
            Child = panel
        };
    }

    public static Control Build(DumpNode node, int depth) => node.Kind switch
    {
        "null" => Text("null", MutedBrush, italic: true),
        "scalar" => Text(node.Value ?? "", ValueBrush),
        "error" => Text(node.Value ?? "error", ErrorBrush),
        "object" => BuildObject(node, depth),
        "table" => Collapsible(node, depth, BuildTable),
        "list" => Collapsible(node, depth, BuildList),
        _ => Text("?" + node.Kind, ErrorBrush)
    };

    /// <summary>Deeply nested composites start collapsed behind an expander.</summary>
    private static Control Collapsible(DumpNode node, int depth, Func<DumpNode, int, Control> builder)
    {
        if (depth < 2) return builder(node, depth);
        return new Expander
        {
            Header = Summary(node),
            IsExpanded = false,
            Content = builder(node, depth)
        };
    }

    private static string Summary(DumpNode node)
    {
        var count = node.Count is int n ? $" ({n}{(node.Truncated == true ? "+" : "")})" : "";
        return (node.TypeName ?? node.Kind) + count;
    }

    private static Control BuildObject(DumpNode node, int depth)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        int row = 0;
        foreach (var p in node.Props ?? [])
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = Text(p.Name, NameBrush);
            name.Margin = new Thickness(0, 2, 12, 2);
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 0);
            var value = Build(p.Value, depth + 1);
            value.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
            row++;
        }

        return Framed(node, grid);
    }

    private static Control BuildTable(DumpNode node, int depth)
    {
        var cols = node.Columns ?? [];
        var grid = new Grid();
        for (int c = 0; c < cols.Count; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < cols.Count; c++)
        {
            var header = new Border
            {
                Background = HeaderBg,
                Padding = new Thickness(8, 4),
                Child = Text(cols[c], NameBrush, bold: true)
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, c);
            grid.Children.Add(header);
        }

        int row = 1;
        foreach (var r in node.Rows ?? [])
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cols.Count && c < r.Count; c++)
            {
                var cell = new Border
                {
                    Background = row % 2 == 0 ? AltRowBg : Brushes.Transparent,
                    Padding = new Thickness(8, 3),
                    Child = Build(r[c], depth + 1)
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            row++;
        }

        return Framed(node, grid);
    }

    private static Control BuildList(DumpNode node, int depth)
    {
        var panel = new StackPanel { Spacing = 2 };
        int i = 0;
        foreach (var item in node.Items ?? [])
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(Text($"[{i++}]", MutedBrush));
            line.Children.Add(Build(item, depth + 1));
            panel.Children.Add(line);
        }
        return Framed(node, panel);
    }

    private static Control Framed(DumpNode node, Control content)
    {
        var panel = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        var caption = Summary(node);
        if (!string.IsNullOrEmpty(caption))
            panel.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = TypeBrush,
                FontSize = 11,
                FontFamily = Mono
            });
        panel.Children.Add(new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Left
        });
        return panel;
    }

    private static TextBlock Text(string text, IBrush brush, bool bold = false, bool italic = false) => new()
    {
        Text = text,
        Foreground = brush,
        FontFamily = Mono,
        FontSize = 13,
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
}
