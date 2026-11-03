using System.Windows;

namespace PdfViewer;

/// <summary>Asks for the password of an encrypted PDF file.</summary>
public partial class PasswordWindow : Window
{
    /// <summary>The entered password (valid after the dialog returned true).</summary>
    public string Password { get; private set; } = "";

    public PasswordWindow(string fileName, bool wasWrong)
    {
        InitializeComponent();
        MessageText.Text = $"The file \"{fileName}\" is password-protected.\nPlease enter the password to open it:";
        ErrorText.Visibility = wasWrong ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Password;
        DialogResult = true;
    }
}
