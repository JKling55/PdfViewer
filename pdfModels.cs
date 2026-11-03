using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace PdfViewer;

/// <summary>
/// One page in the view. It always has a small low-resolution bitmap (placeholder).
/// Pages near the viewport additionally get a high-resolution bitmap that matches
/// the current zoom level and screen DPI, so text stays sharp. The layout size
/// is fixed (derived from the page size in points), so swapping bitmaps never
/// changes the layout.
/// </summary>
public sealed class PageItem : INotifyPropertyChanged
{
    private BitmapSource _source;

    public PageItem(BitmapSource low, double widthPt, double heightPt)
    {
        Low = low;
        _source = low;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    public BitmapSource Low { get; }

    /// <summary>Page size in PDF points (1/72 inch).</summary>
    public double WidthPt { get; }
    public double HeightPt { get; }

    /// <summary>Layout size in WPF units (1/96 inch) at 100 % zoom.</summary>
    public double Width => WidthPt * 96.0 / 72.0;
    public double Height => HeightPt * 96.0 / 72.0;

    /// <summary>Pixels per point of the high-resolution bitmap, or 0 while the placeholder is shown.</summary>
    public double HighScale { get; private set; }

    public BitmapSource Source
    {
        get => _source;
        private set
        {
            _source = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
        }
    }

    public void SetHigh(BitmapSource bitmap, double scale)
    {
        HighScale = scale;
        Source = bitmap;
    }

    /// <summary>Drops the high-resolution bitmap to free memory.</summary>
    public void ResetToLow()
    {
        HighScale = 0;
        Source = Low;
    }

    // ───────────── view rotation ─────────────

    private double _rotation;

    /// <summary>Clockwise rotation of this page in the view: 0, 90, 180 or 270 degrees (display only).</summary>
    public double Rotation
    {
        get => _rotation;
        private set { _rotation = value; Raise(nameof(Rotation)); }
    }

    public void SetRotation(double degrees) => Rotation = degrees;

    // ───────────── search highlights ─────────────

    private Geometry? _highlights;
    private Geometry? _currentHighlight;

    /// <summary>Marks for all search hits on this page (layout units, origin = top-left of the page).</summary>
    public Geometry? Highlights
    {
        get => _highlights;
        private set { _highlights = value; Raise(nameof(Highlights)); }
    }

    /// <summary>Mark for the hit that is currently selected in the result list.</summary>
    public Geometry? CurrentHighlight
    {
        get => _currentHighlight;
        private set { _currentHighlight = value; Raise(nameof(CurrentHighlight)); }
    }

    public void SetHighlights(IEnumerable<Rect> rectsPt) => Highlights = ToGeometry(rectsPt);

    public void SetCurrentHighlight(IEnumerable<Rect>? rectsPt) =>
        CurrentHighlight = rectsPt == null ? null : ToGeometry(rectsPt);

    public void ClearHighlights()
    {
        Highlights = null;
        CurrentHighlight = null;
    }

    // Converts rectangles in points to one frozen geometry in layout units.
    private static Geometry? ToGeometry(IEnumerable<Rect> rectsPt)
    {
        const double k = 96.0 / 72.0;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };

        foreach (var r in rectsPt)
        {
            var p = Rect.Inflate(r, 0.5, 0.5); // a little padding around the glyphs
            group.Children.Add(new RectangleGeometry(new Rect(p.X * k, p.Y * k, p.Width * k, p.Height * k)));
        }

        if (group.Children.Count == 0) return null;
        group.Freeze();
        return group;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Entry in the table of contents (bookmark).</summary>
public class OutlineItem
{
    public string Title { get; set; } = "";
    public int Page { get; set; }   // 1-based, 0 = no jump target
    public List<OutlineItem> Children { get; set; } = new();
}

/// <summary>Search hit with page number and a text snippet.</summary>
public record SearchHit(int Page, string Snippet, int Start)
{
    public string Display => $"p. {Page}: {Snippet}";

    /// <summary>
    /// Where the hit sits on its page: rectangles in PDF points, relative to the top-left
    /// corner of the page (one per line). Filled in after the search.
    /// </summary>
    public Rect[] Rects { get; set; } = Array.Empty<Rect>();
}

/// <summary>
/// Renders pages only when printing (at high resolution), so that not all
/// print pages have to be held in memory at the same time.
/// </summary>
public sealed class PdfPaginator : DocumentPaginator
{
    private readonly IDocReader _reader;
    private readonly IReadOnlyList<int>? _pages; // 0-based pages to print, in print order (null = all)

    public PdfPaginator(IDocReader reader, Size pageSize, IReadOnlyList<int>? pages = null)
    {
        _reader = reader;
        PageSize = pageSize;
        _pages = pages;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pages?.Count ?? _reader.GetPageCount();
    public override Size PageSize { get; set; }
    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        using var pageReader = _reader.GetPageReader(_pages?[pageNumber] ?? pageNumber);
        int w = pageReader.GetPageWidth();
        int h = pageReader.GetPageHeight();
        // No RenderFlags.RenderAnnotations: see PageRenderFlags in PdfTabView.
        byte[] raw = pageReader.GetImage(); // BGRA

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, raw, w * 4);
        bmp.Freeze();

        // Fit the page proportionally into the printable area.
        double scale = Math.Min(PageSize.Width / w, PageSize.Height / h);
        double dw = w * scale, dh = h * scale;
        var target = new Rect((PageSize.Width - dw) / 2, (PageSize.Height - dh) / 2, dw, dh);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // White background underneath, since the rendered page is transparent.
            dc.DrawRectangle(Brushes.White, null, new Rect(PageSize));
            dc.DrawImage(bmp, target);
        }

        var full = new Rect(PageSize);
        return new DocumentPage(visual, PageSize, full, full);
    }
}

/// <summary>
/// PDFium (behind Docnet) is not thread-safe, so every operation that uses it
/// (rendering on load, printing) must hold this gate. With several tabs open,
/// documents are therefore loaded one after another.
/// </summary>
public static class PdfiumLock
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
