using System.IO;
using System.Windows;

namespace LetsSSL.App.Views;

public partial class ExportCertificateWindow : Window
{
    public enum ExportFormat { Pfx, Pem }

    private readonly string _domain;

    public ExportCertificateWindow(string domain)
    {
        InitializeComponent();
        _domain = domain;
        DomainText.Text = $"{domain} — installed certificate";
    }

    public ExportFormat Format => PemRadio.IsChecked == true ? ExportFormat.Pem : ExportFormat.Pfx;
    public string? Password => PasswordBox.Text;
    public string OutputPath => PathBox.Text.Trim();

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        // Fired during InitializeComponent before fields exist; guard against nulls.
        if (PasswordPanel is null || PemHint is null || PathBox is null) return;

        var isPfx = Format == ExportFormat.Pfx;
        PasswordPanel.Visibility = isPfx ? Visibility.Visible : Visibility.Collapsed;
        PemHint.Visibility = isPfx ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(PathBox.Text))
            PathBox.Text = Path.ChangeExtension(PathBox.Text, isPfx ? ".pfx" : ".pem");
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var isPfx = Format == ExportFormat.Pfx;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SafeFileName(_domain) + (isPfx ? ".pfx" : ".pem"),
            DefaultExt = isPfx ? ".pfx" : ".pem",
            Filter = isPfx ? "PFX certificate (*.pfx)|*.pfx" : "PEM certificate (*.pem)|*.pem",
        };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            MessageBox.Show("Choose where to save the file.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string SafeFileName(string domain)
    {
        var name = string.Concat((domain ?? "certificate").Split(Path.GetInvalidFileNameChars()));
        return name.Replace("*", "wildcard").Trim('.', ' ') is { Length: > 0 } s ? s : "certificate";
    }
}
