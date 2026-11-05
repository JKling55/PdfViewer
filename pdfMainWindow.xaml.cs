using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace PdfViewer;

public partial class MainWindow : Window
{
    private const string BaseTitle = "Klingeling PdfViewer";

    public MainWindow()
    {
        InitializeComponent();
        ApplyInitialSize();
        RebuildRecentMenu();
        UpdateTabState();
    }

    // Single-instance mode: full screen. Multiple-instance mode: half of the screen
    // (the left half, or the right half for every second running instance).
    private void ApplyInitialSize()
    {
        if (AppSettings.SingleInstance)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        int others = 0;
        try
        {
            using var me = Process.GetCurrentProcess();
            others = Process.GetProcessesByName(me.ProcessName).Length - 1;
        }
        catch { /* not critical: use the left half */ }

        var area = SystemParameters.WorkArea; // screen without taskbar, in WPF units
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = area.Width / 2;
        Height = area.Height;
        Top = area.Top;
        Left = others % 2 == 1 ? area.Left + area.Width / 2 : area.Left;
    }

    // Remember the open files, so they can be reopened at the next start.
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        var files = Tabs.Items.OfType<TabItem>()
            .Select(t => (t.Content as PdfTabView)?.FilePath)
            .OfType<string>()
            .ToList();
        AppSettings.SaveSession(files);
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        new OptionsWindow { Owner = this }.ShowDialog();
    }

    /// <summary>Called when another program start (single-instance mode) hands over files.</summary>
    public void OpenFromOtherInstance(IEnumerable<string> paths)
    {
        OpenFiles(paths);

        // Bring this window to the front (the Topmost toggle gets around Windows' focus rules).
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = true;
        Topmost = false;
        Activate();
    }

    // The viewer of the currently selected tab (null if no tab is open).
    private PdfTabView? CurrentView => (Tabs.SelectedItem as TabItem)?.Content as PdfTabView;

    // ───────────────────────── Open ─────────────────────────

    /// <summary>Opens PDF files passed on the command line (Explorer "Open with", file association).</summary>
    public void OpenFiles(IEnumerable<string> paths, bool addToRecent = true)
    {
        foreach (string arg in paths)
        {
            try
            {
                string path = Path.GetFullPath(arg);
                if (File.Exists(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    OpenInTab(path, addToRecent);
                else
                    AppLog.Write($"Command line argument ignored (not an existing PDF file): {arg}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Command line argument failed: {arg}", ex);
            }
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenFilesWithDialog();

    private void OpenFilesWithDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf",
            Multiselect = true // several files at once, one tab each
        };
        if (dlg.ShowDialog() != true) return;

        foreach (string file in dlg.FileNames)
            OpenInTab(file);
    }

    /// <summary>Opens the file in a new tab, or switches to its tab if it is already open.</summary>
    private void OpenInTab(string path, bool addToRecent = true)
    {
        AppLog.Write($"OPEN {path}");
        if (addToRecent) RecentFiles.Add(path);

        // Already open? Just select that tab instead of loading the file twice.
        foreach (TabItem existing in Tabs.Items.OfType<TabItem>())
        {
            if (existing.Content is PdfTabView v &&
                string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = existing;
                return;
            }
        }

        var view = new PdfTabView();
        // Declared first, because the header's close button needs a reference to its own tab.
        TabItem tab = null!;
        tab = new TabItem { Content = view, Header = CreateHeader(path, () => tab) };

        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateTabState();

        // Fire and forget: LoadAsync handles its own errors. Several documents
        // are rendered one after another (see PdfiumLock).
        _ = LoadTabAsync(view, tab, path);
    }

    // Loads the document; if the user cancels the password prompt, the empty tab is removed again.
    private async Task LoadTabAsync(PdfTabView view, TabItem tab, string path)
    {
        await view.LoadAsync(path);
        if (view.PasswordCancelled) CloseTab(tab);
    }

    // Builds the tab header: file name + close button; middle-click also closes.
    private UIElement CreateHeader(string path, Func<TabItem> getTab)
    {
        var title = new TextBlock
        {
            Text = Path.GetFileName(path),
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var close = new Button
        {
            Content = "✕",
            ToolTip = "Close (Ctrl+W)",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Focusable = false
        };
        close.Click += (_, _) => CloseTab(getTab());

        var header = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = path };
        header.Children.Add(title);
        header.Children.Add(close);
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                CloseTab(getTab());
                e.Handled = true;
            }
        };
        return header;
    }

    // ───────────────────────── Close ─────────────────────────

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabItem tab) CloseTab(tab);
    }

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        // Iterate over a copy, because closing removes items from the collection.
        foreach (TabItem tab in Tabs.Items.Cast<TabItem>().ToList())
            CloseTab(tab);
    }

    private void CloseTab(TabItem tab)
    {
        // Cancels a running load and frees the rendered pages.
        (tab.Content as PdfTabView)?.Dispose();
        Tabs.Items.Remove(tab);
        UpdateTabState();
    }

    private void Print_Click(object sender, RoutedEventArgs e) => CurrentView?.Print();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────── Previous files ─────────────────────────

    // The submenu is rebuilt every time the File menu opens, so it is always up to date.
    private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, FileMenu)) return;

        PrintItem.IsEnabled = CurrentView?.CanPrint == true; // only for a fully loaded document
        RebuildRecentMenu();
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();

        var files = RecentFiles.Items;
        if (files.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        foreach (string path in files)
        {
            var item = new MenuItem
            {
                // A single "_" in a menu header would be taken as an access key, so double it.
                Header = Path.GetFileName(path).Replace("_", "__"),
                ToolTip = path,
                Tag = path
            };
            item.Click += RecentItem_Click;
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new Separator());

        var openAll = new MenuItem { Header = "Open _All" };
        openAll.Click += (_, _) =>
        {
            // Snapshot first; keep the list order (addToRecent: false).
            foreach (string p in RecentFiles.Items.ToList())
                OpenRecent(p, addToRecent: false);
        };
        RecentMenu.Items.Add(openAll);

        var clear = new MenuItem { Header = "_Clear List" };
        clear.Click += (_, _) => RecentFiles.Clear();
        RecentMenu.Items.Add(clear);
    }

    private void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
            OpenRecent(path, addToRecent: true);
    }

    private void OpenRecent(string path, bool addToRecent)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"The file no longer exists:\n{path}", "Open Previous Files",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RecentFiles.Remove(path);
            return;
        }
        OpenInTab(path, addToRecent);
    }

    // ───────────────────────── Help ─────────────────────────

    // Opens the folder with the diagnostic log files in Explorer.
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            Process.Start(new ProcessStartInfo(AppLog.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open Log Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    // ───────────────────────── Tab state ─────────────────────────

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged of nested controls (e.g. the side tabs) bubbles up to here.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;

        // Diagnostics: every switch of the document tab is logged, so unexpected jumps can be traced.
        string Name(System.Collections.IList items) =>
            items.Count > 0 && items[0] is TabItem { Content: PdfTabView v }
                ? Path.GetFileName(v.FilePath ?? "?") : "(none)";
        AppLog.Write($"TAB switched: {Name(e.RemovedItems)} -> {Name(e.AddedItems)}");

        string? file = CurrentView?.FilePath;
        Title = file == null ? BaseTitle : $"{Path.GetFileName(file)} – {BaseTitle}";
    }

    private void UpdateTabState()
    {
        bool any = Tabs.Items.Count > 0;
        EmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        CloseTabItem.IsEnabled = any;
        CloseAllItem.IsEnabled = any;
    }

    // ───────────────────────── Keyboard shortcuts ─────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F3 / Shift+F3: next / previous search hit
        if (e.Key == Key.F3 &&
            (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            CurrentView?.FindNext(forward: Keyboard.Modifiers == ModifierKeys.None);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        // Window-level shortcuts (work without an open document)
        if (e.Key == Key.O)
        {
            OpenFilesWithDialog();
            e.Handled = true;
            return;
        }

        // Everything else acts on the current document.
        var view = CurrentView;
        if (view == null) return;

        switch (e.Key)
        {
            case Key.W:
                CloseTab_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.P:
                view.Print();
                e.Handled = true;
                break;
            case Key.F:
                view.FocusSearch();
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                view.ChangeZoom(0.1);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                view.ChangeZoom(-0.1);
                e.Handled = true;
                break;
            case Key.Home:
                view.GoToFirstPage();
                e.Handled = true;
                break;
            case Key.End:
                view.GoToLastPage();
                e.Handled = true;
                break;
        }
        // Ctrl+Tab / Ctrl+Shift+Tab to switch tabs is built into the TabControl.
    }

    // ───────────────────────── Drag & drop ─────────────────────────

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        // Accept dropped files (shows the "copy" cursor), reject everything else.
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        foreach (string file in files.Where(f =>
                     f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            OpenInTab(file);
    }
}
