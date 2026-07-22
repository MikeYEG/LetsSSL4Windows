using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LetsSSL.App.ViewModels;
using LetsSSL.Core.Iis;
using LetsSSL.Core.Models;

namespace LetsSSL.App.Views;

public partial class NewCertificateWindow : Window
{
    private readonly NewCertificateViewModel _vm;
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // Danger

    public NewCertificateWindow(NewCertificateViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return; // highlights the missing fields and stays open
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Checks the required fields for the chosen validation method, highlighting
    /// any that are missing in red. Returns true only when the form is complete.
    /// </summary>
    private bool Validate()
    {
        ClearHighlights();
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(_vm.PrimaryDomain))
        {
            Highlight(PrimaryDomainBox);
            missing.Add("Primary domain");
        }
        if (string.IsNullOrWhiteSpace(_vm.ContactEmail))
        {
            Highlight(ContactEmailBox);
            missing.Add("Contact email");
        }

        if (_vm.UseDns)
        {
            // Cloudflare automation needs an API token; Route 53 needs AWS keys; Manual needs nothing here.
            if (_vm.SelectedDnsProvider == DnsProviderType.Cloudflare && string.IsNullOrWhiteSpace(_vm.DnsApiToken))
            {
                Highlight(DnsApiTokenBox);
                missing.Add("Cloudflare API token");
            }
            else if (_vm.SelectedDnsProvider == DnsProviderType.Route53)
            {
                if (string.IsNullOrWhiteSpace(_vm.AwsAccessKeyId))
                {
                    Highlight(AwsAccessKeyBox);
                    missing.Add("AWS Access Key ID");
                }
                if (string.IsNullOrWhiteSpace(_vm.AwsSecretAccessKey))
                {
                    Highlight(AwsSecretBox);
                    missing.Add("AWS Secret Access Key");
                }
            }
        }
        else
        {
            // HTTP-01 needs somewhere to serve the challenge file: a web root or an IIS site.
            if (!_vm.SelectedSites.Any() && string.IsNullOrWhiteSpace(_vm.WebRootPath))
            {
                Highlight(WebRootBox);
                Highlight(SitesDropToggle);
                missing.Add("a web root or an IIS site");
            }
        }

        if (missing.Count == 0)
        {
            ValidationMessage.Visibility = Visibility.Collapsed;
            return true;
        }

        ValidationMessage.Text = missing.Count == 1
            ? $"Please complete the highlighted field: {missing[0]}."
            : $"Please complete the highlighted fields: {string.Join(", ", missing)}.";
        ValidationMessage.Visibility = Visibility.Visible;
        return false;
    }

    private static void Highlight(Control control)
    {
        control.BorderBrush = ErrorBrush;
        control.BorderThickness = new Thickness(2);
    }

    private void ClearHighlights()
    {
        foreach (var control in new Control[] { PrimaryDomainBox, ContactEmailBox, DnsApiTokenBox, AwsAccessKeyBox, AwsSecretBox, WebRootBox, SitesDropToggle })
        {
            control.ClearValue(BorderBrushProperty);
            control.ClearValue(BorderThicknessProperty);
        }
    }

    // Clear a field's red highlight as soon as the user starts fixing it.
    private void OnRequiredFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not Control control) return;
        control.ClearValue(BorderBrushProperty);
        control.ClearValue(BorderThicknessProperty);
        if (ValidationMessage is { Visibility: Visibility.Visible })
            ValidationMessage.Visibility = Visibility.Collapsed;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnTestDnsCredentials(object sender, RoutedEventArgs e) => await _vm.TestDnsCredentialsAsync();

    private void OnAddRemoteTarget(object sender, RoutedEventArgs e) => _vm.AddRemoteTarget();

    private void OnRemoveRemoteTarget(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RemoteTargetViewModel target })
            _vm.RemoveRemoteTarget(target);
    }

    // Pre-flight a remote target over WinRM and report reachability + its IIS sites.
    private async void OnTestRemoteTarget(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RemoteTargetViewModel row }) return;

        var model = row.ToModel();
        if (model is null)
        {
            row.TestSucceeded = false;
            row.TestStatus = "Enter a host name first.";
            return;
        }

        row.IsTesting = true;
        row.TestStatus = $"Connecting to {model.Host} over WinRM…";
        try
        {
            var result = await new RemoteIisDeployer().TestConnectionAsync(model);
            row.TestSucceeded = result.Succeeded;
            row.TestStatus = result.Message;
        }
        catch (System.Exception ex)
        {
            row.TestSucceeded = false;
            row.TestStatus = $"Test failed: {ex.Message}";
        }
        finally
        {
            row.IsTesting = false;
        }
    }
}
