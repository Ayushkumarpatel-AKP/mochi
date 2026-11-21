using Mochi.Services;
using Mochi.Views;

namespace Mochi.ViewModels;

public partial class MainViewModel
{
    public string[] ToolbarStyles => new[]
    {
        Loc.TryGet("x.style_panel") ?? "Quick Panel: search, suggestions, pins, all tools",
        Loc.TryGet("x.style_dock") ?? "Dock: one row of big icons",
        Loc.TryGet("x.style_command") ?? "Command Bar: type what you want, press Enter",
        Loc.TryGet("x.style_pad") ?? "Tile Pad: big colourful tiles with tabs",
        Loc.TryGet("x.style_donut") ?? "Classic donut",
    };

    public int ToolbarStyleIndex
    {
        get => Math.Max(0, Array.IndexOf(Toolbar.Styles, App.Settings.ToolbarStyle));
        set { App.Settings.ToolbarStyle = Toolbar.Styles[Math.Clamp(value, 0, Toolbar.Styles.Length - 1)]; App.Settings.Save(); OnPropertyChanged(); }
    }
}
