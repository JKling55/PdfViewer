using System.Windows;

namespace PdfViewer;

public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();

        SingleRadio.IsChecked = AppSettings.SingleInstance;
        MultiRadio.IsChecked = !AppSettings.SingleInstance;
        ReopenRadio.IsChecked = AppSettings.ReopenLastFiles;
        EmptyRadio.IsChecked = !AppSettings.ReopenLastFiles;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SaveOptions(SingleRadio.IsChecked == true, ReopenRadio.IsChecked == true);
        DialogResult = true;
    }
}
