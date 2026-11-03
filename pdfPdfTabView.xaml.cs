using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace PdfViewer;

/// <summary>
/// Viewer for a single PDF document (one instance per tab).
/// Owns its own pages, outline, search results, zoom and scroll position.
/// </summary>
public partial class PdfTabView : UserControl
{
    // Rendering strategy:
    //  1. On load, every page is rendered once at a low resolution (placeholder, cheap).
    //  2. Pages near the viewport are re-rendered at exactly the resolution needed for the
    //     current zoom level and screen DPI (see RefreshVisiblePagesAsync), so text stays sharp.
    //  3. Pages far away from the viewport drop their high-resolution bitmap again.
    private const double LowScale = 1.0;            // placeholder: pixels per PDF point (72 dpi)
    private const double DipPerPt = 96.0 / 72.0;    // WPF units (1/96 in) per PDF point (1/72 in)
    private const double Oversample = 1.5;          // render a bit larger, WPF then scales down smoothly
    private const double MaxPixelsPerPage = 36_000_000; // memory cap for huge pages (~144 MB each)
    // Render flags for PDFium. RenderFlags.RenderAnnotations is deliberately NOT used: Docnet then
    // creates and destroys a PDFium form-fill environment for EVERY page it renders, which PDFium
    // is not designed for (one per document) and which can crash the program in native code.
    // Without it, annotations (comments, filled-in form fields) are not drawn.
    private const RenderFlags PageRenderFlags = 0; // no flags (RenderFlags.None does not exist in Docnet 2.6.0)
    private const double MaxBitmapSide = 8192;      // longest bitmap side; larger textures can crash the WPF renderer on some GPUs
    // Printing: 300 dpi
    private const double PrintScale = 300.0 / 72.0;
    private const int MaxHits = 500;
    private const double PageMargin = 8;        // top margin of the page template in the XAML (Margin="0,8")

    private readonly ObservableCollection<PageItem> _pages = new();
    private readonly ObservableCollection<SearchHit> _hits = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _refreshTimer;   // debounces scroll/zoom before re-rendering
    private CancellationTokenSource? _refreshCts;     // cancels a running high-res refresh

    private string? _path;
    private string[]? _pageTexts;   // cached page texts for searching
    private bool _loading;
    private bool _searching;
    private int _currentPage;       // 1-based
    private int _rotation;          // view rotation in degrees (0, 90, 180, 270), applies to all pages
    private PageItem? _currentHitPage; // page that shows the orange mark of the selected hit

    // A TabControl detaches the content of inactive tabs from the visual tree,
    // which resets the ScrollViewer. We remember the offset and restore it on Loaded.
    private double _lastVOffset, _lastHOffset;
    private bool _restorePending;

    public PdfTabView()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refreshTimer.Tick += RefreshTimer_Tick;

        InitializeComponent();
        Pages.ItemsSource = _pages;
        HitsList.ItemsSource = _hits;
    }

    public string? FilePath => _path;
    public bool IsLoading => _loading;
    public int PageCount => _pages.Count;

    /// <summary>Stops a running load and frees the rendered pages (call when the tab is closed).</summary>
    public void Dispose()
    {
        AppLog.Write($"TAB closed: {LogName}");
        _refreshTimer.Stop();
        _cts.Cancel(); // also cancels the linked refresh token
        _pages.Clear();
        _hits.Clear();
        _pageTexts = null;
    }

    // ───────────────────────── Status & log ─────────────────────────

    private string LogName => _path == null ? "?" : Path.GetFileName(_path);

    // Shows a message in the status bar and writes it to the log file as well,
    // so the log shows what the program was doing at any time.
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        AppLog.Write($"STATUS [{LogName}] {text}");
    }

    // ───────────────────────── Load ─────────────────────────

    // ───────────────────────── Password-protected files ─────────────────────────

    // Password of the document (null = none needed). Only kept in memory, never saved.
    private string? _password;

    /// <summary>True if the user cancelled the password prompt (the tab should then be closed).</summary>
    public bool PasswordCancelled { get; private set; }

    // PDFium reports "password required or incorrect password" as error code 4.
    private const uint PasswordErrorCode = 4;

    private static PdfDocument OpenPdfPig(string path, string? password) =>
        PdfDocument.Open(path, new ParsingOptions { Password = password ?? "" });

    // Opens the document with PDFium; asks for the password (again) while it is missing or wrong.
    // Runs on a background thread, the prompt itself is shown on the UI thread.
    private Docnet.Core.Readers.IDocReader OpenReaderAskingPassword(string path, double scale)
    {
        while (true)
        {
            try
            {
                return DocLib.Instance.GetDocReader(path, _password, new PageDimensions(scale));
            }
            catch (DocnetLoadDocumentException ex) when (ex.ErrorCode == PasswordErrorCode)
            {
                AppLog.Write($"LOAD password required: {Path.GetFileName(path)} (previous attempt: {_password != null})");
                bool wrong = _password != null;
                string? entered = Dispatcher.Invoke(() =>
                {
                    var dlg = new PasswordWindow(Path.GetFileName(path), wrong)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    return dlg.ShowDialog() == true ? dlg.Password : null;
                });

                if (entered == null)
                {
                    PasswordCancelled = true;
                    throw new OperationCanceledException(); // handled in LoadAsync
                }
                _password = entered;
            }
        }
    }

    public async Task LoadAsync(string path)
    {
        _path = path;
        string name = Path.GetFileName(path);
        var ct = _cts.Token;

        _loading = true;
        PageBox.Text = "0";
        PageCountText.Text = "/ 0";
        OutlineEmpty.Visibility = Visibility.Visible;

        // PDFium is not thread-safe: only one document may render at a time.
        bool waiting = PdfiumLock.Gate.CurrentCount == 0;
        SetStatus(waiting
            ? $"{name} – waiting for another document to finish loading …"
            : $"Loading {name} …");

        bool locked = false;
        try
        {
            AppLog.Write($"LOAD start: {path} ({new FileInfo(path).Length / 1024} KB), {AppLog.Memory()}");

            await PdfiumLock.Gate.WaitAsync(ct);
            locked = true;
            AppLog.Write("LOAD gate acquired, rendering placeholders");

            // The bookmarks (PdfPig) are read while PDFium renders. Started only when this document
            // is open (after a password, if one was needed), so opening many files at once
            // does not parse all of them in parallel.
            Task<List<OutlineItem>>? outlineTask = null;

            // Render low-resolution placeholders in the background and hand over one page
            // at a time to the UI, so the first page appears right away.
            await Task.Run(() =>
            {
                using var docReader = OpenReaderAskingPassword(path, LowScale);
                outlineTask = Task.Run(() => ReadOutline(path, _password));

                int count = docReader.GetPageCount();
                AppLog.Write($"LOAD document opened: {count} pages");
                Dispatcher.Invoke(() => PageCountText.Text = $"/ {count}");

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested(); // tab was closed

                    // Logged BEFORE rendering: if the program dies inside PDFium,
                    // this is the last line of the log.
                    AppLog.Write($"LOAD render page {i + 1}/{count}");
                    using var pageReader = docReader.GetPageReader(i);
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    byte[] raw = pageReader.GetImage(PageRenderFlags); // BGRA

                    var bmp = BitmapSource.Create(
                        w, h, 96, 96, PixelFormats.Bgra32, null, raw, w * 4);
                    bmp.Freeze(); // required to pass the bitmap to the UI thread

                    // Page size in points = pixels / scale.
                    var item = new PageItem(bmp, w / LowScale, h / LowScale);
                    if ((i + 1) % 25 == 0) AppLog.Write(AppLog.Memory());

                    int pageNo = i + 1;
                    Dispatcher.Invoke(() =>
                    {
                        item.SetRotation(_rotation);
                        _pages.Add(item);
                        if (_pages.Count == 1) SetCurrentPage(1);
                        SetStatus($"{name} – loading page {pageNo} of {count}");
                    });
                }
            }, ct);

            AppLog.Write("LOAD placeholders done, document closed");

            var outline = await outlineTask!;
            AppLog.Write($"LOAD outline ready ({outline.Count} top-level entries)");
            OutlineTree.ItemsSource = outline;
            OutlineEmpty.Visibility = outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            SetStatus($"{name} – {_pages.Count} pages");
        }
        catch (OperationCanceledException)
        {
            // Tab was closed while loading: nothing to report to the user.
            AppLog.Write(PasswordCancelled ? $"LOAD cancelled (no password): {name}" : $"LOAD cancelled: {name}");
            if (PasswordCancelled) SetStatus("Password required – file not opened");
        }
        catch (Exception ex)
        {
            AppLog.Error($"LOAD failed: {name}", ex);
            MessageBox.Show(ex.Message, $"Error opening {name}",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Error opening file");
        }
        finally
        {
            if (locked) PdfiumLock.Gate.Release();
            _loading = false;
            AppLog.Write($"LOAD end: {name}, {_pages.Count} pages loaded, {AppLog.Memory()}");
        }

        // Now that the PDFium gate is free, render the visible pages in full resolution.
        RequestRefresh();
    }

    // ───────────────────────── Sharp rendering ─────────────────────────

    // (Re)starts the debounce timer: the refresh only runs once scrolling/zooming has settled.
    private void RequestRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        await RefreshVisiblePagesAsync();
    }

    // Does this page need a (new) high-resolution bitmap for the wanted scale?
    private static bool NeedsRender(PageItem page, double wanted) =>
        wanted > LowScale * 1.05 &&                    // the placeholder is not sharp enough
        (page.HighScale == 0 ||                        // no high-res bitmap yet
         page.HighScale < wanted * 0.99 ||             // zoomed in since
         page.HighScale > wanted * 1.6);               // zoomed out a lot: re-render smaller to save memory

    private async Task RefreshVisiblePagesAsync()
    {
        if (_path == null || _pages.Count == 0 || !IsLoaded) return;

        // A newer request replaces a running one.
        _refreshCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _refreshCts = cts;
        var ct = cts.Token;
        string path = _path;

        bool locked = false;
        try
        {
            // Wait for PDFium (e.g. while this or another tab is still loading).
            // Everything below is evaluated afterwards, so it reflects the current view.
            await PdfiumLock.Gate.WaitAsync(ct);
            locked = true;

            if (!IsLoaded) return; // tab was switched away in the meantime

            var (first, last) = GetVisibleRange();
            if (first < 0) return;

            // Pixels per point that make one bitmap pixel match one screen pixel (times Oversample).
            double wanted = ZoomSlider.Value * DipPerPt
                            * VisualTreeHelper.GetDpi(this).DpiScaleX * Oversample;

            // Cap the scale so that even the largest visible page stays within the memory budget.
            double maxAreaPt = 1, maxSidePt = 1;
            for (int i = first; i <= last; i++)
            {
                maxAreaPt = Math.Max(maxAreaPt, _pages[i].WidthPt * _pages[i].HeightPt);
                maxSidePt = Math.Max(maxSidePt, Math.Max(_pages[i].WidthPt, _pages[i].HeightPt));
            }
            wanted = Math.Min(wanted, Math.Sqrt(MaxPixelsPerPage / maxAreaPt)); // memory budget
            wanted = Math.Min(wanted, MaxBitmapSide / maxSidePt);               // texture size limit

            // Free high-res bitmaps of pages that are far away (or not needed when zoomed out).
            for (int i = 0; i < _pages.Count; i++)
            {
                bool outside = i < first || i > last;
                if (_pages[i].HighScale > 0 && (outside || wanted <= LowScale * 1.05))
                    _pages[i].ResetToLow();
            }

            // Render the pages that need it, starting with the ones closest to the current page.
            int center = Math.Clamp(_currentPage - 1, first, last);
            var targets = Enumerable.Range(first, last - first + 1)
                .Where(i => NeedsRender(_pages[i], wanted))
                .OrderBy(i => Math.Abs(i - center))
                .Select(i => (Index: i, Item: _pages[i]))
                .ToList();
            if (targets.Count == 0) return;

            AppLog.Write($"REFRESH start: pages {first + 1}-{last + 1}, rendering {targets.Count} page(s) " +
                         $"at {wanted:F2} px/pt, zoom {ZoomSlider.Value:P0}, {AppLog.Memory()}");

            await Task.Run(() =>
            {
                using var docReader = DocLib.Instance.GetDocReader(path, _password, new PageDimensions(wanted));

                foreach (var (index, item) in targets)
                {
                    ct.ThrowIfCancellationRequested(); // scrolled/zoomed again, or tab closed

                    AppLog.Write($"REFRESH render page {index + 1}"); // logged before rendering, see above
                    using var pageReader = docReader.GetPageReader(index);
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    byte[] raw = pageReader.GetImage(PageRenderFlags); // BGRA

                    var bmp = BitmapSource.Create(
                        w, h, 96, 96, PixelFormats.Bgra32, null, raw, w * 4);
                    bmp.Freeze();

                    Dispatcher.Invoke(() =>
                    {
                        if (!ct.IsCancellationRequested) item.SetHigh(bmp, wanted);
                    });
                }
            }, ct);
            AppLog.Write($"REFRESH done: {targets.Count} page(s)");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh, or the tab was closed.
            AppLog.Write("REFRESH cancelled");
        }
        catch (Exception ex)
        {
            // Not fatal: the placeholder bitmap just stays visible.
            AppLog.Error("REFRESH failed", ex);
            SetStatus("Sharp rendering failed: " + ex.Message);
        }
        finally
        {
            if (locked) PdfiumLock.Gate.Release();
        }
    }

    // TransformToVisual throws if the element is not (yet / any more) part of the Scroller's
    // visual tree, which can happen while pages are being added or the tab is being detached.
    // Such an exception inside a UI event would end the program, so it is caught here.
    private bool TryGetVerticalSpan(FrameworkElement el, out double top, out double bottom)
    {
        try
        {
            var t = el.TransformToVisual(Scroller);
            top = t.Transform(new Point(0, 0)).Y;
            bottom = t.Transform(new Point(0, el.ActualHeight)).Y;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Write($"WARN TransformToVisual failed: {ex.Message}");
            top = bottom = 0;
            return false;
        }
    }

    // Returns the index range of pages that are visible or within one viewport height
    // above/below (so that they are already sharp when scrolled into view).
    private (int First, int Last) GetVisibleRange()
    {
        double vh = Scroller.ViewportHeight;
        int first = -1, last = -1;

        for (int i = 0; i < _pages.Count; i++)
        {
            if (Pages.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el)
                continue;

            if (!TryGetVerticalSpan(el, out double top, out double bottom))
                continue;

            if (bottom >= -vh && top <= 2 * vh)
            {
                if (first < 0) first = i;
                last = i;
            }
            else if (first >= 0)
            {
                break; // pages are ordered top to bottom, we are past the range
            }
        }
        return (first, last);
    }

    // ───────────────────────── Table of contents ─────────────────────────

    private static List<OutlineItem> ReadOutline(string path, string? password)
    {
        try
        {
            AppLog.Write($"OUTLINE start: {Path.GetFileName(path)}");
            using var doc = OpenPdfPig(path, password);
            if (!doc.TryGetBookmarks(out Bookmarks? bookmarks))
            {
                AppLog.Write("OUTLINE none");
                return new List<OutlineItem>();
            }

            var items = bookmarks!.Roots.Select(ToOutlineItem).ToList();
            AppLog.Write($"OUTLINE done: {items.Count} top-level entries");
            return items;
        }
        catch
        {
            // Broken or missing outline: just show no table of contents.
            return new List<OutlineItem>();
        }
    }

    // Converts a PdfPig bookmark node (recursively) into our own tree item.
    // Only bookmarks that point to a page in the document get a jump target.
    private static OutlineItem ToOutlineItem(BookmarkNode node) => new()
    {
        Title = node.Title,
        Page = (node as DocumentBookmarkNode)?.PageNumber ?? 0,
        Children = node.Children.Select(ToOutlineItem).ToList()
    };

    private void Outline_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineItem { Page: > 0 } item)
            GoToPage(item.Page);
    }

    // ───────────────────────── Page navigation ─────────────────────────

    public void GoToPage(int page)
    {
        if (_pages.Count == 0 || !IsLoaded) return;
        page = Math.Clamp(page, 1, _pages.Count);

        // Make sure the item containers exist and are laid out before measuring.
        Scroller.UpdateLayout();
        if (Pages.ItemContainerGenerator.ContainerFromIndex(page - 1) is FrameworkElement el)
        {
            // Top of the page relative to the visible area; add it to the current offset.
            if (!TryGetVerticalSpan(el, out double y, out _)) return;
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + y);
            SetCurrentPage(page);
        }
    }

    public void GoToFirstPage() => GoToPage(1);
    public void GoToLastPage() => GoToPage(_pages.Count);

    private void SetCurrentPage(int page)
    {
        _currentPage = page;
        // Don't overwrite the text while the user is typing a page number.
        if (!PageBox.IsKeyboardFocusWithin)
            PageBox.Text = page.ToString();
    }

    // Determines the current page: the first page whose bottom edge
    // lies below the vertical center of the viewport.
    private void UpdateCurrentPageFromScroll()
    {
        if (_pages.Count == 0 || !IsLoaded) return;

        double mid = Scroller.ViewportHeight / 2;
        for (int i = 0; i < _pages.Count; i++)
        {
            if (Pages.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el)
                continue;

            if (!TryGetVerticalSpan(el, out _, out double bottom))
                continue;
            if (bottom >= mid)
            {
                SetCurrentPage(i + 1);
                return;
            }
        }
        SetCurrentPage(_pages.Count);
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsLoaded) return;

        // Remember the offset, but only while the layout is valid and no restore is pending
        // (right after re-attaching, the offset is briefly 0).
        if (!_restorePending && Scroller.ViewportHeight > 0 && Scroller.ExtentHeight > 0)
        {
            _lastVOffset = Scroller.VerticalOffset;
            _lastHOffset = Scroller.HorizontalOffset;
        }

        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
        {
            UpdateCurrentPageFromScroll();
            if (!_restorePending) RequestRefresh(); // new pages may have scrolled into view
        }
    }

    // Called when the tab content is detached (another tab selected / tab closed).
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        _restorePending = true;
        _refreshTimer.Stop();
    }

    // Called when the tab content is attached again: restore scroll position.
    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_restorePending) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Scroller.UpdateLayout();
            Scroller.ScrollToVerticalOffset(_lastVOffset);
            Scroller.ScrollToHorizontalOffset(_lastHOffset);
            _restorePending = false;
            UpdateCurrentPageFromScroll();
            RequestRefresh();
        });
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(_currentPage - 1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(_currentPage + 1);

    private void PageBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Move focus away from the TextBox so it follows the current page again.
            Scroller.Focus();
            if (int.TryParse(PageBox.Text, out int page))
                GoToPage(page);
            else
                PageBox.Text = _currentPage.ToString();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Cancel editing and restore the current page number.
            Scroller.Focus();
            PageBox.Text = _currentPage.ToString();
            e.Handled = true;
        }
    }

    // Clicking into the page area gives it keyboard focus (arrow keys, PageUp/PageDown).
    private void Scroller_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Scroller.Focus();

    // ───────────────────────── Rotation ─────────────────────────

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

    /// <summary>Rotates all pages of this document in the view (the file itself is not changed).</summary>
    public void Rotate(int degrees)
    {
        if (_pages.Count == 0) return;

        int page = _currentPage;
        _rotation = ((_rotation + degrees) % 360 + 360) % 360;
        foreach (var p in _pages) p.SetRotation(_rotation);
        AppLog.Write($"ROTATE [{LogName}] view rotation is now {_rotation} degrees");

        // The page sizes change, so after the new layout keep the same page in view.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            GoToPage(page);
            RequestRefresh();
        });
    }

    // Top edge (in points from the top of the rotated page) of a rectangle given in
    // unrotated page coordinates. Needed to scroll to a search hit on a rotated page.
    private double TopInRotatedPage(PageItem page, Rect r) => _rotation switch
    {
        90 => r.Left,
        180 => page.HeightPt - r.Bottom,
        270 => page.WidthPt - r.Right,
        _ => r.Top
    };

    // ───────────────────────── Zoom ─────────────────────────

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider can fire during InitializeComponent, before the transform exists.
        if (ZoomTransform == null) return;

        // Pages have a fixed layout size (page size at 100 %), so the zoom factor
        // is applied directly. The sharp bitmaps are re-rendered once zooming settles.
        ZoomTransform.ScaleX = ZoomSlider.Value;
        ZoomTransform.ScaleY = ZoomSlider.Value;
        ZoomText.Text = $"{ZoomSlider.Value:P0}";
        RequestRefresh();
    }

    public void ChangeZoom(double delta) =>
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + delta, ZoomSlider.Minimum, ZoomSlider.Maximum);

    // Ctrl + mouse wheel zooms; without Ctrl the wheel scrolls as usual.
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        ChangeZoom(e.Delta > 0 ? 0.1 : -0.1);
        e.Handled = true;
    }

    // ───────────────────────── Search ─────────────────────────

    /// <summary>Switches to the search tab and focuses the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SideTabs.SelectedIndex = 1;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        string term = SearchBox.Text.Trim();
        if (_path == null || _searching) return;
        if (_loading)
        {
            SearchStatus.Text = "Please wait, the document is still loading …";
            return;
        }

        // Remove the previous results and their marks on the pages.
        ClearSearchResults();
        if (term.Length == 0)
        {
            SearchStatus.Text = "";
            return;
        }

        _searching = true;
        SearchStatus.Text = "Searching …";

        try
        {
            string path = _path;
            // Extract the text of all pages once and cache it for later searches.
            _pageTexts ??= await Task.Run(() => ExtractTexts(path, _password));
            string[] texts = _pageTexts;

            // Find the hits, then look up where each hit sits on its page (for the marks).
            var hits = await Task.Run(() =>
            {
                var found = FindHits(texts, term);
                AttachRects(path, _password, term, found);
                return found;
            });

            foreach (var h in hits) _hits.Add(h);
            ApplyHighlights(hits);

            if (hits.Count > 0)
                SearchStatus.Text = hits.Count >= MaxHits ? $"{MaxHits}+ hits"
                    : hits.Count == 1 ? "1 hit" : $"{hits.Count} hits";
            else if (texts.All(string.IsNullOrWhiteSpace))
                // No text layer at all: most likely a scanned document.
                SearchStatus.Text = "No searchable text (possibly a scanned PDF).";
            else
                SearchStatus.Text = "No hits";
        }
        catch (Exception ex)
        {
            SearchStatus.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            _searching = false;
        }
    }

    private static string[] ExtractTexts(string path, string? password)
    {
        using var doc = OpenPdfPig(path, password);
        // Same text model that is used for the hit positions (see PageText).
        return doc.GetPages().Select(p => PageText.Build(p).Text).ToArray();
    }

    // Finds all case-insensitive occurrences of the term, page by page,
    // and builds a short snippet around each hit (capped at MaxHits).
    private static List<SearchHit> FindHits(string[] texts, string term)
    {
        var list = new List<SearchHit>();
        for (int p = 0; p < texts.Length && list.Count < MaxHits; p++)
        {
            string t = texts[p];
            int idx = 0;
            while (list.Count < MaxHits &&
                   (idx = t.IndexOf(term, idx, StringComparison.CurrentCultureIgnoreCase)) >= 0)
            {
                int start = Math.Max(0, idx - 30);
                int len = Math.Min(t.Length - start, term.Length + 60);
                string snippet = t.Substring(start, len).Replace('\r', ' ').Replace('\n', ' ');
                list.Add(new SearchHit(p + 1,
                    (start > 0 ? "…" : "") + snippet + (start + len < t.Length ? "…" : ""),
                    idx));
                idx += term.Length;
            }
        }
        return list;
    }

    // Looks up the glyph positions of every hit. Each page is analyzed once.
    private static void AttachRects(string path, string? password, string term, List<SearchHit> hits)
    {
        using var doc = OpenPdfPig(path, password);
        foreach (var onPage in hits.GroupBy(h => h.Page))
        {
            try
            {
                var pageText = PageText.Build(doc.GetPage(onPage.Key));
                foreach (var hit in onPage)
                    hit.Rects = pageText.GetRects(hit.Start, term.Length).ToArray();
            }
            catch
            {
                // Page can't be analyzed: the hit stays in the list, just without a mark.
            }
        }
    }

    // Draws the (yellow) marks of all hits onto their pages.
    private void ApplyHighlights(IEnumerable<SearchHit> hits)
    {
        foreach (var onPage in hits.GroupBy(h => h.Page))
        {
            if (onPage.Key < 1 || onPage.Key > _pages.Count) continue; // tab was closed meanwhile
            _pages[onPage.Key - 1].SetHighlights(onPage.SelectMany(h => h.Rects));
        }
    }

    private void ClearSearchResults()
    {
        _hits.Clear();
        _currentHitPage = null;
        foreach (var page in _pages) page.ClearHighlights();
    }

    // Marks the hit in orange and scrolls it into view.
    private void ShowHit(SearchHit hit)
    {
        if (hit.Page < 1 || hit.Page > _pages.Count) return;

        _currentHitPage?.SetCurrentHighlight(null);
        var item = _pages[hit.Page - 1];
        item.SetCurrentHighlight(hit.Rects);
        _currentHitPage = item;

        if (hit.Rects.Length == 0)
            GoToPage(hit.Page); // position unknown: show the top of the page
        else
            ScrollToPageY(hit.Page, TopInRotatedPage(item, hit.Rects[0]));
    }

    // Scrolls so that the given y position (in points from the top of the page)
    // ends up in the upper third of the viewport.
    private void ScrollToPageY(int page, double yPt)
    {
        if (!IsLoaded) return;

        Scroller.UpdateLayout();
        if (Pages.ItemContainerGenerator.ContainerFromIndex(page - 1) is not FrameworkElement el)
            return;

        if (!TryGetVerticalSpan(el, out double top, out _)) return;
        double y = top + (PageMargin + yPt * DipPerPt) * ZoomTransform.ScaleY;
        double target = Scroller.VerticalOffset + y - Scroller.ViewportHeight / 3;
        Scroller.ScrollToVerticalOffset(Math.Max(0, target));
    }

    /// <summary>Selects the next / previous search hit (F3 / Shift+F3), wrapping around at the ends.</summary>
    public void FindNext(bool forward)
    {
        if (_hits.Count == 0) return;

        int i = HitsList.SelectedIndex;
        i = forward ? (i + 1) % _hits.Count : (i <= 0 ? _hits.Count - 1 : i - 1);

        HitsList.SelectedIndex = i;
        HitsList.ScrollIntoView(_hits[i]);
        ShowHit(_hits[i]); // also when the selection did not change (single hit)
    }

    private void Hit_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (HitsList.SelectedItem is SearchHit hit)
            ShowHit(hit);
    }

    // Clicking the already selected hit does not raise SelectionChanged, so the click is
    // handled as well: the user can jump back to the hit after scrolling away.
    private void HitItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SearchHit hit })
            ShowHit(hit);
    }

    // ───────────────────────── Printing ─────────────────────────

    /// <summary>True when the document is fully loaded and can be printed.</summary>
    public bool CanPrint => _path != null && !_loading && _pages.Count > 0;

    public void Print()
    {
        if (!CanPrint) return;

        // Step 1: which pages? (own dialog: any selection such as "1, 3, 7-9")
        var pagesDlg = new PrintPagesWindow(_pages.Count) { Owner = Window.GetWindow(this) };
        if (pagesDlg.ShowDialog() != true) return;
        List<int>? selected = pagesDlg.SelectedPages; // 0-based, null = all pages

        // Step 2: printer and its settings (standard Windows dialog)
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        // PDFium must not be used by two threads at once: if another tab
        // is currently rendering, ask the user to try again in a moment.
        if (!PdfiumLock.Gate.Wait(0))
        {
            MessageBox.Show("Another document is currently loading. Please try again in a moment.",
                "Print", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // For printing, the document is re-rendered at 300 dpi, one page at a time.
            using var reader = DocLib.Instance.GetDocReader(_path, _password, new PageDimensions(PrintScale));
            var paginator = new PdfPaginator(
                reader, new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight), selected);
            dlg.PrintDocument(paginator, Path.GetFileName(_path));
            SetStatus(selected == null ? "Print job sent" : $"Print job sent ({selected.Count} pages)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Print error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PdfiumLock.Gate.Release();
        }
    }
}
