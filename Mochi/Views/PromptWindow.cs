using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>A small dialog with one text box per label (some masked), used for passwords, page ranges and clip times.</summary>
public sealed class PromptWindow : Window
{
    private string[]? _result;

    public PromptWindow(string title, string[] labels, string[] defaults, bool[] secret)
    {
        Title = title; Width = 460; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var boxes = new List<TextBox>();
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        for (var i = 0; i < labels.Length; i++)
        {
            panel.Children.Add(new TextBlock { Text = labels[i], Classes = { "headline" } });
            var tb = new TextBox { Text = defaults[i], PasswordChar = secret[i] ? '•' : '\0' };
            boxes.Add(tb); panel.Children.Add(tb);
        }
        var ok = new Button { Content = Loc.T("common.ok"), Classes = { "pill" }, IsDefault = true };
        var cancel = new Button { Content = Loc.T("common.cancel") };
        ok.Click += (_, _) => { _result = boxes.Select(b => b.Text ?? "").ToArray(); Close(); };
        cancel.Click += (_, _) => Close();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, ok } });
        Content = panel;
        Opened += (_, _) => boxes[0].Focus();
    }

    public static Task<string[]?> AskAsync(string title, string[] labels, string[] defaults, bool[] secret)
    {
        var tcs = new TaskCompletionSource<string[]?>();
        var w = new PromptWindow(title, labels, defaults, secret) { Topmost = true };
        w.Closed += (_, _) => tcs.TrySetResult(w._result);
        w.Show();
        return tcs.Task;
    }
}
