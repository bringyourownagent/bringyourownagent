using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Byoa.Wallet.Desktop;

/// <summary>Small builders for the wallet's windows, so that the code reads like the layout.</summary>
static class Ui
{
    public static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    public static FontFamily SignFace => (FontFamily)Application.Current!.FindResource("SignFace")!;

    /// <summary>The picture in a window's title bar and the task switcher: the multi-size .ico on Windows and macOS, a PNG on Linux.</summary>
    public static WindowIcon AppIcon => new(Avalonia.Platform.AssetLoader.Open(new Uri(
        OperatingSystem.IsLinux() ? "avares://Byoa.Wallet/Assets/icon-256.png" : "avares://Byoa.Wallet/Assets/app.ico")));

    /// <summary>A text the visitor can select and copy (see <see cref="WalletText"/>).</summary>
    public static TextBlock Text(string text, params string[] classes)
    {
        var t = new WalletText { Text = text, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    /// <summary>A text that is not selected: the label of a window's title bar, which drags the window.</summary>
    public static TextBlock Plain(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    public static TextBlock Wrap(string text, params string[] classes)
    {
        var t = Text(text, classes);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    /// <summary>A text with bold and plain runs: ("**host**", " wants to use your AI").</summary>
    public static TextBlock Runs(params (string Text, bool Bold)[] parts)
    {
        var t = new WalletText { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (text, bold) in parts)
        {
            t.Inlines!.Add(new Avalonia.Controls.Documents.Run(text) { FontWeight = bold ? FontWeight.Bold : FontWeight.Normal });
        }

        return t;
    }

    public static Button Button(string text, string kind, string automationId, Action onClick, params string[] more)
    {
        var b = new Button { Content = text };
        b.Classes.Add(kind);
        foreach (var c in more) b.Classes.Add(c);
        AutomationProperties.SetAutomationId(b, automationId);
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Link(string text, string automationId, Action onClick, bool underline = false)
    {
        var b = new Button { Content = new TextBlock { Text = text, TextDecorations = underline ? TextDecorations.Underline : null } };
        b.Classes.Add("link");
        AutomationProperties.SetAutomationId(b, automationId);
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static Border Box(string kind, Control child, Thickness? margin = null)
    {
        var b = new Border { Child = child };
        b.Classes.Add(kind);
        if (margin is { } m) b.Margin = m;
        return b;
    }

    public static Border Chip(string text, string kind = "")
    {
        var b = new Border { Child = new TextBlock { Text = text }, HorizontalAlignment = HorizontalAlignment.Left };
        b.Classes.Add("chip");
        if (kind.Length > 0) b.Classes.Add(kind);
        return b;
    }

    public static Border Rule(double top = 4, double bottom = 4) => new Border { Classes = { "rule" }, Margin = new Thickness(0, top, 0, bottom) };

    // ------------------------------------------------------------------------------------------------
    // glyphs: plain stroke drawings, 24 units

    public const string Globe = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z M2,12 L22,12 M12,2 C16,6 16,18 12,22 M12,2 C8,6 8,18 12,22";
    public const string Screen = "M3,4 L21,4 L21,16 L3,16 Z M8,20 L16,20 M12,16 L12,20";
    public const string Key = "M8,9 A4,4 0 1 0 8,17 A4,4 0 1 0 8,9 Z M12,13 L21,13 M18,13 L18,16 M21,13 L21,16";
    public const string Check = "M5,12 L10,17 L19,7";
    public const string Cross = "M6,6 L18,18 M18,6 L6,18";
    public const string Arrow = "M2,12 L20,12 M14,6 L20,12 L14,18";
    public const string Up = "M6,15 L12,9 L18,15";
    public const string Down = "M6,9 L12,15 L18,9";
    public const string Minus = "M6,12 L18,12";
    public const string Gauge = "M3,17 A9,9 0 0 1 21,17 M12,17 L16,10";
    public const string List = "M5,6 L19,6 M5,12 L19,12 M5,18 L14,18";
    public const string Plus = "M6,12 L18,12 M12,6 L12,18";
    public const string Refresh = "M23,4 L23,10 L17,10 M1,20 L1,14 L7,14 M3.51,9 A9,9 0 0 1 18.36,5.64 L23,10 M1,14 L5.64,18.36 A9,9 0 0 0 20.49,15";

    /// <summary>A stroke drawing on a 24-unit canvas, scaled to <paramref name="size"/> (straight lines keep their place).</summary>
    public static Control Glyph(string data, IBrush stroke, double size = 16, double thickness = 2.2)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
    }

    /// <summary>A square sign-blue tile with a white drawing (the site's favicon stand-in).</summary>
    public static Border Tile(string glyph, double size = 24, string background = "Sign") => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size >= 30 ? 6 : 5),
        Background = Brush(background),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Viewbox { Margin = new Thickness(size * 0.2), Child = Glyph(glyph, Brushes.White, 24, 2.2) },
    };

    /// <summary>A small ink circle with a white check: "this meets every need".</summary>
    public static Border InkCheck(double size = 16) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = Brush("Ink"),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Viewbox { Margin = new Thickness(size * 0.22), Child = Glyph(Check, Brushes.White, 24, 3.2) },
    };

    /// <summary>A numbered stop of a fallback route: the first filled Exit Orange, a paid one dashed.</summary>
    public static Border Stop(int number, bool first, bool paid)
    {
        var b = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(paid ? 1.5 : 2),
            BorderBrush = first ? Brush("SignalDeep") : paid ? Brush("Outline") : Brush("Sign"),
            Background = first ? Brush("SignalDeep") : Brushes.White,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontFamily = SignFace,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                Foreground = first ? Brushes.White : Brush("Sign"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return b;
    }

    public static Grid Columns(string definition, params Control[] children)
    {
        var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(definition) };
        for (var i = 0; i < children.Length; i++)
        {
            Grid.SetColumn(children[i], i);
            g.Children.Add(children[i]);
        }

        return g;
    }
}
