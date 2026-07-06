using System.Collections.Generic;
using System.Windows;
using LetsSSL.Core.Iis;

namespace LetsSSL.App.Views;

public partial class BindIisWindow : Window
{
    /// <summary>The IIS site the user chose to bind to (set when the dialog returns true).</summary>
    public string? SelectedSiteName { get; private set; }

    public BindIisWindow(string certName, IReadOnlyList<IisSiteInfo> sites)
    {
        InitializeComponent();

        IntroText.Text = $"Add or update an HTTPS (SNI) binding for “{certName}” on the site below. " +
                         "This site will also be re-bound automatically on future renewals.";

        SiteCombo.ItemsSource = sites;
        if (sites.Count > 0) SiteCombo.SelectedIndex = 0;
    }

    private void OnBind(object sender, RoutedEventArgs e)
    {
        if (SiteCombo.SelectedItem is not IisSiteInfo site)
        {
            MessageBox.Show("Select an IIS site to bind to.", "Bind to IIS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedSiteName = site.Name;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
