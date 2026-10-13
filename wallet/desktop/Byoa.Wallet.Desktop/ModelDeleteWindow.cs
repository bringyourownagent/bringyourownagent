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

    public ModelDeleteWindow(string modelName, long? sizeBytes, string? note = null, Func<int>? waitingCount = null)
    {
        Title = "Delete downloaded model";
        Width = 540;
        MinWidth = 480;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        WindowLayout.Prepare(this, 220);

        var question = Ui.Wrap("Delete " + modelName + " (" + Size(sizeBytes) + ")? You can download it again.", "h2");
        question.Width = 480;
        AutomationProperties.SetAutomationId(question, "ModelDeleteQuestion");
        var delete = Ui.Button("Delete", "allow", "ModelDeleteConfirm", () => Answer(true), "small");
        var cancel = Ui.Button("Cancel", "deny", "ModelDeleteCancel", () => Answer(false), "small");
        var waiting = waitingCount?.Invoke() is { } count && count > 1
            ? Ui.Wrap((count - 1).ToString() + " more waiting.", "muted", "small")
            : Ui.Text("", "small");
        var body = Ui.Column(14,
            Ui.Text("Delete downloaded model", "h1"),
            question,
            note is null ? Ui.Text("", "small") : Ui.Wrap(note, "muted", "small"),
            Ui.Wrap("Only a model downloaded by this wallet can be removed.", "muted"),
            waiting);
        body.Width = 480;
        body.Margin = new Thickness(30, 26, 30, 24);
        var buttons = Ui.Row(8, delete, cancel);
        buttons.Margin = new Thickness(30, 0, 30, 24);
        Content = Ui.Column(0, WindowLayout.Body(this, body, 92, 220), buttons);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Answer(false); }
            else if (e.Key is Key.Enter or Key.Space) e.Handled = true;
        };
        Opened += (_, _) => { Activate(); Focus(); };
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
            : ((long)Math.Ceiling(bytes.Value / 1_000_000d)).ToString(CultureInfo.InvariantCulture) + " MB";
    }

    void Answer(bool answer)
    {
        _result.TrySetResult(answer);
        if (!_closed) DesktopUi.PostSafe(Close);
    }
}
