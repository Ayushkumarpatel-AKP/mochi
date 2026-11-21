using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Mochi.Services;
using Mochi.ViewModels;

namespace Mochi.Views;

/// <summary>Borderless transparent window that shows the donut at the cursor and closes itself afterwards.</summary>
public sealed class DonutWindow : Window
{
    private const int Size = 470;   // room for the outer ring of an open group
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private DateTime _still = DateTime.UtcNow;
    private Avalonia.PixelPoint _lastCursor;
    private readonly PixelPoint _center;
    private readonly bool _manual;

    public static DonutWindow? Current { get; private set; }

    private DonutControl? _control;
    private DateTime? _awaitDrag;   // an Alt+D donut lost focus to the click that starts a drag in Explorer: keep it for that drag

    /// <summary>True when it stays until the user acts; false for the hover style that closes by itself.</summary>
    public bool Persistent => _manual || _activated;
    private readonly bool _activated;

    /// <summary>Shows the slices that fit these files (Explorer's selection while a drag has just started).</summary>
    public void Suggest(IReadOnlyList<string> files) => _control?.Suggest(files);

    /// <summary>Builds slices from preset ids; an id like "g:abc" is a group. When files are given, only presets that fit them stay.</summary>
    public static List<SliceItem> Build(IEnumerable<string> ids, IReadOnlyCollection<string>? files = null)
    {
        bool Fits(Preset p) => !Ai.Hidden(p) && (files is null || files.DistinctBy(f => Util.Ext(f)).Any(f => Converter.IsSupported(f, p)));   // some are enough: the rest are skipped and reported
        var list = new List<SliceItem>();
        foreach (var id in ids)
        {
            if (id.StartsWith("g:"))
            {
                var g = App.Settings.Groups.FirstOrDefault(x => "g:" + x.Id == id);
                var m = g?.Members.Select(Converter.Find).OfType<Preset>().Where(Fits).ToList();
                if (g is not null && m is { Count: > 0 }) list.Add(new SliceItem(g.Name, null, m));
            }
            else if (Converter.Find(id) is { } p && Fits(p)) list.Add(new SliceItem(p.DisplayName, p, null));
        }
        return list;
    }

    /// <summary>Front and back faces holding only the presets that can convert every one of <paramref name="files"/>.</summary>
    private static (List<SliceItem> Front, List<SliceItem> Back) Faces(IReadOnlyList<string> files)
    {
        var prof = MainViewModel.ProfileFor(files);
        var f = prof is not null ? Build(prof.PresetIds, files) : new();
        if (f.Count == 0) f = Converter.Suggest(files).Select(p => new SliceItem(p.DisplayName, p, null)).ToList();
        // the AI slice goes with every kind of file (it shows only the AI tools that fit them)
        var b = Build(prof is { BackPresetIds.Count: > 0 } ? prof.BackPresetIds : App.Settings.DonutBackIds, files);
        if (Ai.IsConfigured && !f.Any(x => x.IsGroup && x.Label == "✦ AI") && !b.Any(x => x.IsGroup && x.Label == "✦ AI") && Build(new[] { "g:ai" }, files) is { Count: > 0 } ai)
        {
            // a full face gives its last slice to the AI slice (that tool moves to the back face if there is room); nothing is saved
            if (f.Count >= 8) { var pushed = f[^1]; f.RemoveAt(f.Count - 1); if (b.Count < 8) b.Insert(0, pushed); }
            f.AddRange(ai);
        }
        return (f, b);
    }

    /// <param name="pending">Files picked beforehand; a click on a slice converts them.</param>
    /// <param name="front">Explicit ids for the front face (the settings preview).</param>
    public DonutWindow(MainViewModel vm, IReadOnlyList<string>? pending = null, IReadOnlyList<string>? front = null, IReadOnlyList<string>? back = null, bool activate = false)
    {
        Current?.Close();
        Current = this;
        _manual = pending is { Count: > 0 };
        _activated = activate;

        List<SliceItem> f, b;
        if (front is not null) { f = Build(front); b = Build(back ?? App.Settings.DonutBackIds); }
        else if (_manual) (f, b) = Faces(pending!);
        else { f = Build(App.Settings.DonutIds); b = Build(App.Settings.DonutBackIds); }

        var donut = new DonutControl
        {
            Front = f, Back = b,
            Hole = _manual ? Loc.T("x.n_files", pending!.Count) : activate ? Loc.TryGet("x.select_first") ?? "Select files, then press Alt+D. Or drop them here." : Loc.T("x.drop_files"),
            Clicked = p => { if (_manual) Run(vm, p, pending!); },
            Dropped = (p, files) => Run(vm, p, files),
            Refilter = files => { var (ff, bb) = Faces(files); return (ff, bb); },
            Supports = (files, p) => files.All(f => Converter.IsSupported(f, p)),
            NoFiles = kinds => ToastWindow.Notify(new[] { new ViewModels.RecentItem(Loc.TryGet("x.no_files_title") ?? "Nothing to convert", "", string.Format(Loc.TryGet("x.no_files") ?? "That drag did not carry files Mochi can read (it carried: {0}). Save the file to a folder first, then drag it from there.", kinds), true, Array.Empty<string>()) }),
            Missed = files => ToastWindow.Notify(new[] { new ViewModels.RecentItem(Path.GetFileName(files[0].TrimEnd('\\', '/')), files[0], Loc.TryGet("x.drop_on_slice") ?? "Drop the files on one of the slices, not on the middle.", true, Array.Empty<string>()) }),
        };
        _control = donut;

        Width = Size; Height = Size;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = _manual || activate;   // a donut opened from the keyboard takes focus, so Tab can flip it
        CanResize = false;
        Content = donut;

        _center = Native.Cursor();
        var scale = Screens.ScreenFromPoint(_center)?.Scaling ?? 1.0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(_center.X - (int)(Size * scale / 2), _center.Y - (int)(Size * scale / 2));

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Tab or Key.Space or Key.F) { donut.Flip(); e.Handled = true; }
        };
        // opened from the keyboard or with files: take the focus for real (Windows refuses it to a background app), so it stays up and Tab works
        Opened += (_, _) => { if ((_manual || activate) && TryGetPlatformHandle()?.Handle is { } h) { Native.ForceForeground(h); Activate(); } };
        Deactivated += (_, _) =>
        {
            if (!(_manual || activate)) return;
            // an Alt+D donut waits for files dragged onto it, and starting that drag means clicking in Explorer, which takes the focus: stay
            if (activate && !_manual && Native.LeftDown()) { _awaitDrag = DateTime.UtcNow; return; }
            Close();
        };
        Closed += (_, _) => { _watch.Stop(); if (Current == this) Current = null; };

        _watch.Tick += (_, _) =>
        {
            var waiting = activate && !_manual;   // Alt+D with nothing picked: it waits for a drop
            var c = Native.Cursor();
            if (c != _lastCursor) { _lastCursor = c; _still = DateTime.UtcNow; }
            // gone once the pointer leaves the ring (room for an open group's outer ring) or rests untouched for a few seconds;
            // a held button keeps it up so a drag can still arrive
            var dx = c.X - _center.X; var dy = c.Y - _center.Y;
            var away = !waiting && Math.Sqrt((double)dx * dx + (double)dy * dy) > (DonutControl.Outer + DonutControl.Band + 26) * (_manual ? 2.2 : 1) * RenderScaling;   // files already picked: be patient
            var patient = _manual || (_control?.HoldingFiles ?? false);   // files in hand: give time to pick the tool
            var idle = (DateTime.UtcNow - _still).TotalSeconds > (patient ? 12 : waiting ? 20 : 4);
            // the button went down outside the donut while it waits: either a drag is starting in Explorer or it is just a click elsewhere (decided below)
            if (waiting && _awaitDrag is null && Native.LeftDown() && Math.Sqrt((double)dx * dx + (double)dy * dy) > (DonutControl.Outer + DonutControl.Band + 26) * RenderScaling) { _awaitDrag = DateTime.UtcNow; DropLog.Write("donut: button down outside while waiting for a drop"); }
            if (!Native.LeftDown() && _control?.TakeLostDrop() is { } lost) { Run(vm, lost.Tool, lost.Files); return; }
            if (_awaitDrag is { } since)
            {
                if (Native.LeftDown()) { _still = DateTime.UtcNow; return; }                      // the drag is on its way
                if (_control?.DragNear == true) _awaitDrag = null;                               // it arrived
                else if ((DateTime.UtcNow - since).TotalSeconds > 2) { DropLog.Write("donut: that was only a click, closing"); Close(); return; }       // it was only a click
                else return;
            }
            if ((away || idle) && !Native.LeftDown()) Close();
        };
        _watch.Start();
    }

    /// <summary>Closes the donut and runs the tool; the shared runner skips files it cannot convert and reports the rest.</summary>
    private void Run(MainViewModel vm, Preset p, IReadOnlyCollection<string> files)
    {
        Close();
        _ = Toolbar.RunAsync(vm, p, files);
    }
}
