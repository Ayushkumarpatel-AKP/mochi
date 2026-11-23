using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mochi.Services;

namespace Mochi.Views;

/// <summary>The pop-up that says a newer Mochi is out, shows what is new in it, and installs it on one click.</summary>
public sealed class UpdateWindow : Window
{
    private static string L(string key, string fallback) => Loc.TryGet(key) ?? fallback;

    public static void Offer(Updater.Release r) => new UpdateWindow(r).Show();

    private UpdateWindow(Updater.Release r)
    {
        Title = L("x.up.title", "Mochi update");
        Width = 460; SizeToContent = SizeToContent.Height; CanResize = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "secondary" }, IsVisible = false };
        var update = new Button { Content = L("x.up.update_now", "Update now"), Classes = { "pill" }, IsDefault = true, IsEnabled = r.AssetUrl is not null };
        var later = new Button { Content = L("x.up.later", "Later") };
        later.Click += (_, _) => Close();
        update.Click += async (_, _) =>
        {
            update.IsEnabled = later.IsEnabled = false;
            status.IsVisible = true; status.Text = string.Format(L("x.up.downloading", "Downloading {0}…"), r.Version);
            try { await Updater.DownloadAndRunAsync(r); status.Text = L("x.up.installing", "Installing. Mochi restarts by itself when it is done."); }
            catch (Exception ex) { status.Text = string.Format(L("x.up.failed", "Update check failed: {0}"), ex.Message); later.IsEnabled = true; }
        };

        var head = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = string.Format(L("x.up.popup_title", "Mochi {0} is ready"), r.Version), FontSize = 20, FontWeight = FontWeight.Bold },
            new TextBlock { Text = string.Format(L("x.up.popup_sub", "You have {0}. Your settings are kept."), Updater.Current), Classes = { "secondary" } },
        } };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        top.Children.Add(new MascotView { Mascot = "hello", Width = 72, Height = 72 });
        Grid.SetColumn(head, 1); top.Children.Add(head);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(top);
        var notes = Clean(r.Notes);
        if (notes.Length > 0)
            panel.Children.Add(new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Background = new SolidColorBrush(Colors.Gray, 0.12), MaxHeight = 220,
                Child = new ScrollViewer { Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap } } });
        panel.Children.Add(status);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { later, update } });
        Content = panel;
    }

    /// <summary>The release notes as plain lines: headings and bullets without the markdown marks.</summary>
    private static string Clean(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var lines = md.Replace("\r", "").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("Windows 10"))
            .Select(l => l.Trim().TrimStart('#').Trim().Replace("**", "").Replace("`", ""))
            .Select(l => l.StartsWith("- ") ? "•  " + l[2..] : l);
        return string.Join("\n", lines).Trim();
    }
}
