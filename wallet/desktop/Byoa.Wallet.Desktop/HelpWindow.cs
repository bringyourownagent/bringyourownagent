using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Byoa.Wallet.Help;

namespace Byoa.Wallet.Desktop;

/// <summary>Help rendered from the same embedded markdown articles returned by the MCP help tool.</summary>
sealed class HelpWindow : Window
{
    readonly ListBox _topics = new() { SelectionMode = SelectionMode.Single };
    readonly ScrollViewer _body = new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    readonly TextBlock _title = Ui.Text("Help", "h1");
    string _topic = "overview";

    public HelpWindow(string? topic = null)
    {
        Title = "Agent wallet help";
        Width = 900;
        Height = 650;
        MinWidth = 700;
        MinHeight = 480;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = Ui.AppIcon;

        foreach (var name in HelpCatalog.Topics)
        {
            var item = new ListBoxItem { Content = HelpCatalog.Title(name), Tag = name };
            AutomationProperties.SetAutomationId(item, "HelpTopic-" + name);
            AutomationProperties.SetName(item, HelpCatalog.Title(name));
            _topics.Items.Add(item);
        }

        _topics.Classes.Add("sidebar");
        _topics.SelectionChanged += (_, _) =>
        {
            if (_topics.SelectedItem is ListBoxItem { Tag: string selected })
            {
                _topic = selected;
                Render();
            }
        };
        _body.SizeChanged += (_, _) => ResizeBodyToViewport();
        AutomationProperties.SetAutomationId(_topics, "HelpTopics");
        var side = Ui.Column(12, Ui.Text("Help", "h2"), Ui.Wrap("Plain words about the wallet and your AI.", "muted", "small"), _topics);
        side.Margin = new Thickness(16, 18, 12, 16);
        var border = new Border { Child = side, Background = Ui.Brush("Field"), BorderBrush = Ui.Brush("WinRule"), BorderThickness = new Thickness(0, 0, 1, 0), Width = 220 };
        var content = Ui.Column(12, _title, _body);
        content.Margin = new Thickness(28, 22, 28, 26);
        Content = Ui.Columns("Auto,*", border, content);
        _topic = HelpCatalog.Topics.Contains(topic ?? "", StringComparer.Ordinal) ? topic! : "overview";
        _topics.SelectedIndex = Math.Max(0, HelpCatalog.Topics.ToList().IndexOf(_topic));
        Render();
    }

    void Render()
    {
        var markdown = HelpCatalog.Get(_topic);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var panel = new StackPanel { Spacing = 9 };
        foreach (var line in lines)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                _title.Text = line[2..].Trim();
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal)) { panel.Children.Add(Ui.Text(line[3..].Trim(), "h2")); continue; }
            if (line.StartsWith("- ", StringComparison.Ordinal)) { panel.Children.Add(Ui.Wrap("• " + line[2..], "muted")); continue; }
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (line.Length == 0) { panel.Children.Add(new Border { Height = 3 }); continue; }
            panel.Children.Add(Ui.Wrap(line.Replace("`", "", StringComparison.Ordinal), "muted"));
        }
        _body.Content = panel;
        ResizeBodyToViewport();
    }

    void ResizeBodyToViewport()
    {
        if (_body.Content is Control content && _body.Bounds.Width > 0)
            content.Width = _body.Bounds.Width;
    }

}
