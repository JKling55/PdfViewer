using System.Windows;
using System.Windows.Controls;

namespace PdfViewer;

/// <summary>Asks which pages to print. The printer is chosen afterwards in the standard Windows dialog.</summary>
public partial class PrintPagesWindow : Window
{
    private readonly int _pageCount;

    /// <summary>0-based pages to print in the typed order; null = all pages.</summary>
    public List<int>? SelectedPages { get; private set; }

    public PrintPagesWindow(int pageCount)
    {
        InitializeComponent();
        _pageCount = pageCount;
        AllRadio.Content = $"All pages (1–{pageCount})";
    }

    // Typing into the box selects the "Pages" option automatically.
    private void PagesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PagesRadio.IsChecked = true;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (AllRadio.IsChecked == true)
        {
            SelectedPages = null;
        }
        else
        {
            var pages = PageSelection.TryParse(PagesBox.Text, _pageCount, out string error);
            if (pages == null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                PagesBox.Focus();
                return; // keep the dialog open
            }
            SelectedPages = pages;
        }
        DialogResult = true;
    }
}
