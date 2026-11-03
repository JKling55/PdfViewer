using System.Windows;

namespace PdfViewer;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Version comes from <Version> in the project file.
        var version = typeof(AboutWindow).Assembly.GetName().Version;
        VersionText.Text = version == null ? "" : $"Version {version.ToString(3)}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    // Shows the license texts of the bundled third-party libraries.
    private void Licenses_Click(object sender, RoutedEventArgs e) =>
        new LicensesWindow { Owner = this }.ShowDialog();
}
