using Avalonia.Threading;
using Mochi.ViewModels;
using Mochi.Views;

namespace Mochi.Services;

/// <summary>Global triggers, polled: modifier+D, or modifier held while the left button is dragging, opens the donut at the cursor.</summary>
public sealed class Triggers
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private DateTime? _dragSince;
    private Avalonia.PixelPoint _dragFrom;
    private bool _fromExplorer;
    private bool _latched;
    private DateTime? _openSince;

    public bool Paused { get; set; }

    public Triggers(MainViewModel vm)
    {
        _vm = vm;
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        var s = App.Settings;
        Watchdog();
        if (Paused || !s.TriggersEnabled || DonutWindow.Current is not null || Views.ToolbarBase.Current is not null) { if (!Native.LeftDown()) _dragSince = null; return; }

        var mod = Native.Modifier(s.TriggerModifier);
        var combo = mod && Native.Key('D');
        var drag = mod && Native.LeftDown();

        if (!drag) _dragSince = null;
        else if (_dragSince is null) { _dragSince = DateTime.UtcNow; _dragFrom = Native.Cursor(); _fromExplorer = Native.OverExplorer(); }

        // A drag is the pointer actually moving with the button and the key held. Shift and Ctrl clicks are everywhere,
        // so with those the drag has to start in Explorer; Alt has no such everyday use and works anywhere.
        var moved = false;
        if (drag)
        {
            var c = Native.Cursor();
            moved = Math.Abs(c.X - _dragFrom.X) + Math.Abs(c.Y - _dragFrom.Y) >= 14;
        }
        var allowed = Native.Modifier("Alt") && s.TriggerModifier is "Alt" or "Alt or Shift" || _fromExplorer;
        var fire = combo || (drag && moved && allowed && (DateTime.UtcNow - _dragSince!.Value).TotalMilliseconds > 120);
        if (!fire) { _latched = false; return; }
        if (_latched) return;
        _latched = true;
        // Alt+D with files selected in Explorer converts those files; without a selection it waits for a drop
        var selected = combo ? Native.ExplorerSelection() : new List<string>();
        if (selected.Count > 0) Toolbar.Show(_vm, selected, true);
        else
        {
            Toolbar.Show(_vm, null, combo);
            // a drag out of Explorer: what is selected there is what is being dragged, so the donut can suggest the right tools already
            if (!combo && _fromExplorer)
                Dispatcher.UIThread.Post(() =>   // Explorer's selection is read through COM, which wants this (STA) thread
                {
                    if (Native.ExplorerSelection() is { Count: > 0 } files) { if (s.ToolbarStyle == "donut") DonutWindow.Current?.Suggest(files); else Views.ToolbarBase.Current?.Suggest(files); }
                }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// A hover-style donut or toolbar closes itself in seconds; one still up after 40 s with no button held is stuck
    /// (and would silently block every new trigger), so it is closed.
    /// </summary>
    private void Watchdog()
    {
        var d = DonutWindow.Current; var t = Views.ToolbarBase.Current;
        var stuck = d is { Persistent: false } || t is { Persistent: false };
        if (!stuck) { _openSince = null; return; }
        _openSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _openSince.Value).TotalSeconds > 40 && !Native.LeftDown())
        {
            d?.Close(); t?.Close();
            _openSince = null;
        }
    }
}
