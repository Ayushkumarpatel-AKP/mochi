using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Mochi.Services;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig.Writer;

namespace Mochi.Views;

public sealed record PageItem(int File, int Page, Bitmap Thumb, int Rot = 0)
{
    public string Label => $"{Loc.T("x.pdf.page")} {Page}" + (File > 0 ? $"  ({Loc.T("x.pdf.file")} {File + 1})" : "") + (Rot != 0 ? $"  ⟳ {Rot}°" : "");
}

/// <summary>Reorder, delete, duplicate and extract the pages of a PDF, and append pages from other PDFs.</summary>
public sealed class PdfEditorWindow : Window
{
    private readonly List<byte[]> _files = new();
    private readonly ObservableCollection<PageItem> _pages = new();
    private readonly ListBox _list;
    private readonly string _path;
    public string? Saved { get; private set; }

    public PdfEditorWindow(string path)
    {
        _path = path;
        Title = Loc.T("x.pdf.title") + " — " + Path.GetFileName(path);
        Width = 760; Height = 700;

        _list = new ListBox
        {
            ItemsSource = _pages,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<PageItem>((p, _) => p is null ? new Control() : new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(4),
                Children =
                {
                    new Border   // the thumbnail turns with the page; the frame keeps the list rows steady
                    {
                        Width = 120, Height = 120,
                        Child = new Image { Source = p.Thumb, Width = p.Rot % 180 == 0 ? 100 : 100, Height = 100, RenderTransform = new RotateTransform(p.Rot), Stretch = Stretch.Uniform },
                    },
                    new TextBlock { Text = p.Label, VerticalAlignment = VerticalAlignment.Center },
                },
            }),
        };

        Button B(string t, Action a, bool pill = false)
        {
            var b = new Button { Content = t };
            if (pill) b.Classes.Add("pill");
            b.Click += (_, _) => a();
            return b;
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12) };
        bar.Children.Add(B("▲ " + Loc.T("x.pdf.up"), () => Move(-1)));
        bar.Children.Add(B("▼ " + Loc.T("x.pdf.down"), () => Move(1)));
        bar.Children.Add(B("⟲", () => Rotate(-90)));
        bar.Children.Add(B("⟳", () => Rotate(90)));
        bar.Children.Add(B(Loc.T("x.pdf.duplicate"), Duplicate));
        bar.Children.Add(B(Loc.T("common.delete"), Delete));
        bar.Children.Add(B(Loc.T("x.pdf.add"), async () => await AddAsync()));
        bar.Children.Add(B(Loc.T("common.cancel"), Close));
        bar.Children.Add(B(Loc.T("common.save"), Save, true));

        var hint = new TextBlock { Text = Loc.T("x.pdf.hint"), Margin = new Thickness(12, 0), TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } };
        var top = new StackPanel { Children = { bar, hint } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_list);
        Content = root;

        Opened += async (_, _) =>
        {
            try { await LoadAsync(path); }
            catch (Exception ex) { hint.Text = ex.Message; }
        };
    }

    private async Task LoadAsync(string path)
    {
        var idx = _files.Count;
        var bytes = await File.ReadAllBytesAsync(path);
        _files.Add(bytes);
        var thumbs = await Task.Run(() =>
        {
            var l = new List<Bitmap>();
            using var ms = new MemoryStream(bytes);
            foreach (var bmp in Conversion.ToImages(ms, options: new PDFtoImage.RenderOptions(Dpi: 36)))
                using (bmp)
                {
                    using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
                    l.Add(new Bitmap(new MemoryStream(data.ToArray())));
                }
            return l;
        });
        for (var i = 0; i < thumbs.Count(); i++) _pages.Add(new PageItem(idx, i + 1, thumbs[i]));
    }

    private async Task AddAsync()
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });
        foreach (var f in r.Select(x => x.TryGetLocalPath()).OfType<string>()) await LoadAsync(f);
    }

    private List<int> Selected() => _list.Selection.SelectedIndexes.OrderBy(i => i).ToList();

    private void Move(int d)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var order = d < 0 ? sel : Enumerable.Reverse(sel).ToList();
        foreach (var i in order)
        {
            var j = i + d;
            if (j < 0 || j >= _pages.Count || sel.Contains(j)) continue;
            _pages.Move(i, j);
            sel[sel.IndexOf(i)] = j;
        }
        _list.Selection.Clear();
        foreach (var i in sel) _list.Selection.Select(i);
    }

    private void Rotate(int deg)
    {
        var sel = Selected();
        foreach (var i in sel) _pages[i] = _pages[i] with { Rot = ((_pages[i].Rot + deg) % 360 + 360) % 360 };
        _list.Selection.Clear();
        foreach (var i in sel) _list.Selection.Select(i);
    }

    private void Duplicate()
    {
        foreach (var i in Selected().AsEnumerable().Reverse()) _pages.Insert(i + 1, _pages[i]);
    }

    private void Delete()
    {
        foreach (var i in Selected().AsEnumerable().Reverse()) _pages.RemoveAt(i);
    }

    private void Save()
    {
        if (_pages.Count == 0) return;
        // consecutive pages of the same file are written as one run
        var files = new List<byte[]>(); var bundles = new List<IReadOnlyList<int>>();
        foreach (var grp in Runs())
        {
            files.Add(_files[grp.file]);
            bundles.Add(grp.pages);
        }
        var dest = Util.Out(_path, Path.GetDirectoryName(_path)!, ".pdf", " (edited)");
        var merged = PdfMerger.Merge(files, bundles);
        var turns = new Dictionary<int, int>();
        for (var i = 0; i < _pages.Count; i++) if (_pages[i].Rot != 0) turns[i + 1] = _pages[i].Rot;
        if (turns.Count == 0) File.WriteAllBytes(dest, merged);
        else Services.Engines.PdfEngine.RotateSave(merged, dest, turns);
        Saved = dest;
        Close();
    }

    private IEnumerable<(int file, List<int> pages)> Runs()
    {
        (int file, List<int> pages)? cur = null;
        foreach (var p in _pages)
        {
            if (cur is { } c && c.file == p.File) c.pages.Add(p.Page);
            else
            {
                if (cur is not null) yield return cur.Value;
                cur = (p.File, new List<int> { p.Page });
            }
        }
        if (cur is not null) yield return cur.Value;
    }

    public static Task<string?> RunAsync(string path)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new PdfEditorWindow(path);
        w.Closed += (_, _) => tcs.TrySetResult(w.Saved);
        w.Show();
        return tcs.Task;
    }
}
