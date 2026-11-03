using System.IO;
using System.Windows;

namespace PdfViewer;

/// <summary>Shows THIRD-PARTY-NOTICES.txt, which is embedded in the executable.</summary>
public partial class LicensesWindow : Window
{
    private const string ResourceName = "THIRD-PARTY-NOTICES.txt";

    public LicensesWindow()
    {
        InitializeComponent();

        using Stream? stream = typeof(LicensesWindow).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            // Should not happen: the file is embedded by the project file.
            LicenseText.Text = "The license file is missing from this build.";
            return;
        }

        using var reader = new StreamReader(stream);
        LicenseText.Text = reader.ReadToEnd();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
