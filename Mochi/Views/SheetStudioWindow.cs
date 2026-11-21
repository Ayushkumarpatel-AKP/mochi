using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClosedXML.Excel;
using Mochi.Services;
using PDFtoImage;
using SkiaSharp;

namespace Mochi.Views;

/// <summary>Spreadsheet print studio: choose and order sheets, page setup, and see the printed pages before saving the PDF.</summary>
public sealed class SheetStudioWindow : Window
{
    private sealed class SheetItem
    {
        public string Name = ""; public bool Include = true;
    }

    private readonly string _input;
    private string _xlsx = "";                     // an .xlsx copy the page setup is applied to (older formats are converted first)
    private readonly List<SheetItem> _sheets = new();
    private readonly StackPanel _sheetList = new() { Spacing = 4 };
    private readonly ComboBox _orient = new() { ItemsSource = new[] { "Portrait", "Landscape" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _paper = new() { ItemsSource = new[] { "A4", "A3", "Letter", "Legal" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _wide = new() { Minimum = 1, Maximum = 20, Value = 1, FormatString = "0" };
    private readonly NumericUpDown _tall = new() { Minimum = 0, Maximum = 50, Value = 0, FormatString = "0" };
    private readonly NumericUpDown _margin = new() { Minimum = 0, Maximum = 5, Value = 1.5m, Increment = 0.25m, FormatString = "0.00" };
    private readonly CheckBox _content = new();
    private readonly TextBlock _status = new() { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _pages = new() { Spacing = 12 };
    private readonly Button _export = new() { Classes = { "pill" } };
    public string? Saved { get; private set; }

    public SheetStudioWindow(string input)
    {
        _input = input;
        Title = Loc.T("sheet_studio.title") + " — " + Path.GetFileName(input);
        Width = 1120; Height = 760;
        _content.Content = Loc.T("sheet_studio.use_content");

        _orient.ItemsSource = new[] { Loc.T("sheet_studio.portrait"), Loc.T("sheet_studio.landscape") };

        var preview = new Button { Content = Loc.T("x.ss.preview") };
        preview.Click += async (_, _) => await PreviewAsync();
        _export.Content = Loc.T("sheet_studio.export_pdf");
        _export.Click += async (_, _) => await ExportAsync();
        var cancel = new Button { Content = Loc.T("common.cancel") };
        cancel.Click += (_, _) => Close();

        TextBlock H(string t) => new() { Text = t, Classes = { "headline" }, Margin = new Thickness(0, 10, 0, 2) };
        var left = new StackPanel
        {
            Spacing = 4, Margin = new Thickness(16), Width = 320,
            Children =
            {
                H(Loc.T("sheet_studio.sheets")), _sheetList,
                H(Loc.T("sheet_studio.orientation")), _orient,
                H(Loc.T("sheet_studio.paper")), _paper,
                H(Loc.T("sheet_studio.pages_wide")), _wide,
                H(Loc.T("sheet_studio.pages_tall")), _tall,
                H(Loc.T("x.ss.margin")), _margin,
                _content,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, preview, _export } },
                _status,
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new ScrollViewer { Content = left });
        var right = new ScrollViewer { Content = _pages, Margin = new Thickness(8), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        Content = grid;

        Opened += async (_, _) =>
        {
            try { await LoadAsync(); await PreviewAsync(); }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
    }

    private async Task LoadAsync()
    {
        _status.Text = Loc.T("sheet_studio.rendering");
        _xlsx = await Task.Run(() =>
        {
            var e = Util.Ext(_input);
            if (e is ".xlsx" or ".xlsm") return _input;
            var tmp = Util.TempDir();                          // xls, ods and csv go through a conversion to xlsx
            return Converter.Run(_input, Converter.Find("xlsx")!, tmp)[0];
        });
        using var wb = new XLWorkbook(_xlsx);
        foreach (var ws in wb.Worksheets) _sheets.Add(new SheetItem { Name = ws.Name });
        RebuildList();
    }

    private void RebuildList()
    {
        _sheetList.Children.Clear();
        for (var i = 0; i < _sheets.Count; i++)
        {
            var s = _sheets[i]; var idx = i;
            var cb = new CheckBox { Content = s.Name, IsChecked = s.Include, VerticalAlignment = VerticalAlignment.Center };
            cb.IsCheckedChanged += (_, _) => s.Include = cb.IsChecked == true;
            Button Arrow(string t, int d) { var b = new Button { Content = t, Classes = { "plain" }, Padding = new Thickness(6, 0) }; b.Click += (_, _) => Move(idx, d); return b; }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            row.Children.Add(cb);
            var up = Arrow("▲", -1); Grid.SetColumn(up, 1); row.Children.Add(up);
            var dn = Arrow("▼", 1); Grid.SetColumn(dn, 2); row.Children.Add(dn);
            _sheetList.Children.Add(row);
        }
    }

    private void Move(int i, int d)
    {
        var j = i + d;
        if (j < 0 || j >= _sheets.Count) return;
        (_sheets[i], _sheets[j]) = (_sheets[j], _sheets[i]);
        RebuildList();
    }

    /// <summary>Applies the chosen page setup to a copy of the workbook and prints that copy to PDF with LibreOffice.</summary>
    private sealed record Setup(bool Landscape, int Paper, int Wide, int Tall, decimal Margin, bool UseContent, List<string> Order);

    /// <summary>Reads the form on the UI thread; the render runs on another thread and must not touch controls.</summary>
    private Setup Capture() => new(_orient.SelectedIndex == 1, _paper.SelectedIndex, (int)(_wide.Value ?? 1), (int)(_tall.Value ?? 0), _margin.Value ?? 1.5m,
        _content.IsChecked == true, _sheets.Where(s => s.Include).Select(s => s.Name).ToList());

    private string RenderPdf(string dir, Setup c)
    {
        var copy = Path.Combine(dir, "book.xlsx");
        using (var wb = new XLWorkbook(_xlsx))
        {
            var order = c.Order;
            if (order.Count == 0) throw new InvalidOperationException(Loc.T("x.ss.none"));
            foreach (var ws in wb.Worksheets.Where(w => !order.Contains(w.Name)).ToList()) ws.Delete();
            for (var i = 0; i < order.Count; i++) wb.Worksheet(order[i]).Position = i + 1;
            var landscape = c.Landscape;
            var paper = c.Paper switch { 1 => XLPaperSize.A3Paper, 2 => XLPaperSize.LetterPaper, 3 => XLPaperSize.LegalPaper, _ => XLPaperSize.A4Paper };
            var inch = (double)c.Margin / 2.54;
            foreach (var ws in wb.Worksheets)
            {
                var ps = ws.PageSetup;
                ps.PageOrientation = landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
                ps.PaperSize = paper;
                ps.Margins.Top = ps.Margins.Bottom = ps.Margins.Left = ps.Margins.Right = inch;
                if (c.UseContent) ps.PrintAreas.Clear();
                ps.PagesWide = c.Wide;
                ps.PagesTall = c.Tall;
            }
            wb.SaveAs(copy);
        }
        return Converter.Run(copy, Converter.Find("doc-pdf")!, dir)[0];
    }

    private async Task PreviewAsync()
    {
        _status.Text = Loc.T("sheet_studio.rendering");
        var setup = Capture();
        try
        {
            var imgs = await Task.Run(() =>
            {
                var tmp = Util.TempDir();
                try
                {
                    var pdf = RenderPdf(tmp, setup);
                    var bytes = File.ReadAllBytes(pdf);
                    var list = new List<Bitmap>();
                    using var ms = new MemoryStream(bytes);
                    foreach (var bmp in Conversion.ToImages(ms, options: new PDFtoImage.RenderOptions(Dpi: 70)))
                        using (bmp)
                        {
                            using var d = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 85);
                            list.Add(new Bitmap(new MemoryStream(d.ToArray())));
                        }
                    return list;
                }
                finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
            });
            _pages.Children.Clear();
            foreach (var b in imgs)
                _pages.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = new Image { Source = b, Stretch = Stretch.None } });
            _status.Text = Loc.T("sheet_studio.page_count", imgs.Count);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task ExportAsync()
    {
        _export.IsEnabled = false; _status.Text = Loc.T("sheet_studio.exporting");
        var setup = Capture();
        try
        {
            Saved = await Task.Run(() =>
            {
                var tmp = Util.TempDir();
                try
                {
                    var pdf = RenderPdf(tmp, setup);
                    var dest = Util.Out(_input, Chosen() ?? Path.GetDirectoryName(_input)!, ".pdf");
                    File.Copy(pdf, dest);
                    return dest;
                }
                finally { try { Directory.Delete(tmp, true); } catch { /* temp cleanup */ } }
            });
            Close();
        }
        catch (Exception ex) { _status.Text = ex.Message; _export.IsEnabled = true; }
    }

    private static string? Chosen() => string.IsNullOrWhiteSpace(App.Settings.OutputFolder) ? null : App.Settings.OutputFolder;

    public static Task<string?> RunAsync(string path)
    {
        var tcs = new TaskCompletionSource<string?>();
        var w = new SheetStudioWindow(path);
        w.Closed += (_, _) => tcs.TrySetResult(w.Saved);
        w.Show();
        return tcs.Task;
    }
}
