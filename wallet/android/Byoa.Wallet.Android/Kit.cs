using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Byoa.Wallet.Engine;

namespace Byoa.Wallet.Android;

/// <summary>Small builders for the wallet's screens, so that the code reads like the layout (the phone's counterpart of the desktop wallet's <c>Ui</c>).</summary>
static class Kit
{
    public static IBrush Brush(string key) => (IBrush)Avalonia.Application.Current!.FindResource(key)!;

    /// <summary>A text; <paramref name="classes"/> are the style classes of App.axaml ("muted", "small", "strong", "h1"...).</summary>
    public static TextBlock Text(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    /// <summary>A text with bold and plain runs: ("host", bold) (" wants to use your AI", plain).</summary>
    public static TextBlock Runs(params (string Text, bool Bold)[] parts)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (text, bold) in parts)
            t.Inlines!.Add(new Avalonia.Controls.Documents.Run(text) { FontWeight = bold ? FontWeight.Bold : FontWeight.Normal });
        return t;
    }

    /// <summary>A control that a test or a screen reader finds by <paramref name="id"/> and by its <paramref name="name"/>.</summary>
    public static T Named<T>(T control, string id, string? name = null) where T : Control
    {
        AutomationProperties.SetAutomationId(control, id);
        if (name is not null) AutomationProperties.SetName(control, name);
        return control;
    }

    public static Button Button(string text, string kind, string id, Action onClick, params string[] more)
    {
        var b = new Button { Content = text };
        b.Classes.Add(kind);
        foreach (var c in more) b.Classes.Add(c);
        Named(b, id, text);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A button that reads as a link (sign blue, no frame); still 48 dp tall.</summary>
    public static Button Link(string text, string id, Action onClick)
    {
        var b = new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } };
        b.Classes.Add("link");
        Named(b, id, text);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static CheckBox Tick(string text, string id, bool isChecked, Action<bool>? changed = null)
    {
        var box = new CheckBox { IsChecked = isChecked, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } };
        Named(box, id, text);
        if (changed is not null) box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
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

    public static Border Box(string kind, Control child)
    {
        var b = new Border { Child = child };
        b.Classes.Add(kind);
        return b;
    }

    public static Border Chip(string text, string kind = "")
    {
        var b = new Border { Child = new TextBlock { Text = text }, HorizontalAlignment = HorizontalAlignment.Left };
        b.Classes.Add("chip");
        if (kind.Length > 0) b.Classes.Add(kind);
        return b;
    }

    /// <summary>A control that lives longer than the screen's content (a field that keeps what was typed while the list is drawn again) leaves its old place first.</summary>
    public static T Detach<T>(T control) where T : Control
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is Decorator decorator) decorator.Child = null;
        return control;
    }

    /// <summary>
    /// A row of a list with its settings folded under it: the header, a "Settings" link, and the settings (built only while they are open, so a
    /// long list stays light). <paramref name="open"/> remembers which rows are open when the list is drawn again.
    /// </summary>
    public static Border Fold(HashSet<string> open, string key, string id, string name, Control header, Func<Control> details, Action redraw)
    {
        var isOpen = open.Contains(key);
        var toggle = Link(isOpen ? "Hide" : "Settings", id, () =>
        {
            if (!open.Remove(key)) open.Add(key);
            redraw();
        });
        var column = Column(8, Columns("*,Auto", header, toggle));
        if (isOpen) column.Children.Add(details());
        var row = Box("row", column);
        AutomationProperties.SetName(row, name);
        return row;
    }

    /// <summary>A number the visitor changes with a minus and a plus (no keyboard): the label, the value, and the two buttons.</summary>
    public static Control Stepper(string label, string value, string id, Action down, Action up, bool canDown = true, bool canUp = true)
    {
        var minus = Button("", "deny", id + "-down", down, "small");
        minus.Content = Glyph("M6,12 L18,12", Brush("Ink"), 16, 2.6);
        AutomationProperties.SetName(minus, "Lower: " + label);
        minus.IsEnabled = canDown;
        var plus = Button("", "deny", id + "-up", up, "small");
        plus.Content = Glyph("M6,12 L18,12 M12,6 L12,18", Brush("Ink"), 16, 2.6);
        AutomationProperties.SetName(plus, "Raise: " + label);
        plus.IsEnabled = canUp;
        var figure = Text(value, "strong");
        figure.MinWidth = 78;
        figure.TextAlignment = TextAlignment.Center;
        AutomationProperties.SetAutomationId(figure, id + "-value");
        return Column(4, Text(label), Row(6, minus, figure, plus));      // the label has the whole width: on a phone it would be cut down to a few words beside the buttons
    }

    /// <summary>A small button with an arrow drawn on it, up or down (the phone's font has no arrow glyphs: a typed arrow would show as an empty box).</summary>
    public static Button Move(bool up, string label, string id, Action onClick)
    {
        var b = Button("", "deny", id, onClick, "small");
        b.Content = Glyph(up ? "M12,19 L12,5 M6,11 L12,5 L18,11" : "M12,5 L12,19 M6,13 L12,19 L18,13", Brush("Ink"), 16, 2.6);
        AutomationProperties.SetName(b, (up ? "Move up: " : "Move down: ") + label);
        return b;
    }

    /// <summary>A small coloured dot: how recently a site used the wallet.</summary>
    public static Control Dot(string color) => new Ellipse { Width = 11, Height = 11, Fill = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Center };

    public static Border Rule(double top = 4, double bottom = 4) => new Border { Classes = { "rule" }, Margin = new Thickness(0, top, 0, bottom) };

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

    // ------------------------------------------------------------------------------------------------
    // glyphs: plain stroke drawings, 24 units (the same as the desktop wallet's)

    public const string Check = "M5,12 L10,17 L19,7";
    public const string Cross = "M6,6 L18,18 M18,6 L6,18";
    public const string Back = "M15,5 L8,12 L15,19";
    public const string Globe = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z M2,12 L22,12 M12,2 C16,6 16,18 12,22 M12,2 C8,6 8,18 12,22";

    public static Control Glyph(string data, IBrush stroke, double size = 20, double thickness = 2.2)
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
    public static Border Tile(string glyph, double size = 36) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(7),
        Background = Brush("Sign"),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Viewbox { Margin = new Thickness(size * 0.2), Child = Glyph(glyph, Brushes.White, 24, 2.2) },
    };

    /// <summary>A small ink circle with a white check: "this meets every need".</summary>
    public static Border InkCheck(double size = 18) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = Brush("Ink"),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Viewbox { Margin = new Thickness(size * 0.22), Child = Glyph(Check, Brushes.White, 24, 3.2) },
    };

    // ------------------------------------------------------------------------------------------------
    // what the screens say about things

    /// <summary>A host as the visitor reads it: "localhost:8080", not "http://localhost:8080".</summary>
    public static string HostOf(string origin) => Origins.TryParse(origin, out var host, out _) ? host : origin;

    public static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        if (span < TimeSpan.FromSeconds(10)) return "just now";
        if (span < TimeSpan.FromMinutes(1)) return (int)span.TotalSeconds + " s ago";
        if (span < TimeSpan.FromHours(1)) return (int)span.TotalMinutes + " min ago";
        if (span < TimeSpan.FromDays(1)) return at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return at.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }
}
