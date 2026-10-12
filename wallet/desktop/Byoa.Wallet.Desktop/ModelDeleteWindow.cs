using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Byoa.Wallet.Desktop;

/// <summary>Confirms deletion of a model that the wallet downloaded into its own model folder.</summary>
sealed class ModelDeleteWindow : Window
{
    readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _closed;

    public ModelDeleteWindow(string modelName, long? sizeBytes)
    {
        Title = "Delete downloaded model";
        Width = 540;
        Height = 250;
        MinWidth = 480;
        MinHeight = 220;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;

        var question = Ui.Wrap("Delete " + modelName + " (" + Size(sizeBytes) + ")? You can download it again.", "h2");
        question.Width = 480;
        AutomationProperties.SetAutomationId(question, "ModelDeleteQuestion");
        var delete = Ui.Button("Delete", "allow", "ModelDeleteConfirm", () => Answer(true), "small");
        var cancel = Ui.Button("Cancel", "deny", "ModelDeleteCancel", () => Answer(false), "small");
        var content = Ui.Column(14,
            Ui.Text("Delete downloaded model", "h1"),
            question,
            Ui.Wrap("Only a model downloaded by this wallet can be removed.", "muted"),
            Ui.Row(8, delete, cancel));
        content.Width = 480;
        Content = content;
        content.Margin = new Thickness(30, 26, 30, 24);

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Answer(false);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _result.TrySetResult(false);
        };
    }

    public Task<bool> Result => _result.Task;

    static string Size(long? bytes)
    {
        if (bytes is null) return "size unknown";
        return bytes.Value >= 1_000_000_000
            ? (bytes.Value / 1_000_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes.Value / 1_000_000d).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    void Answer(bool answer)
    {
        _result.TrySetResult(answer);
        if (!_closed) DesktopUi.PostSafe(Close);
    }
}
