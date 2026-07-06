using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LetsSSL.App.ViewModels;
using LetsSSL.Core.Dns;

namespace LetsSSL.App.Views;

public partial class ManualDnsWindow : Window
{
    private readonly List<ManualDnsRecordViewModel> _records;
    private readonly DnsTxtVerifier _verifier = new();
    private readonly DispatcherTimer _recheckTimer;
    private bool _checking;

    public ManualDnsWindow(IReadOnlyList<DnsTxtRecord> records)
    {
        InitializeComponent();

        _records = records.Select(r => new ManualDnsRecordViewModel(r)).ToList();
        RecordsList.ItemsSource = _records;

        IntroText.Text = records.Count == 1
            ? "Create the following TXT record at your DNS provider. It's checked automatically — click Continue once it shows as live."
            : $"Create the following {records.Count} TXT records at your DNS provider (a wildcard plus its base domain " +
              "need two records with the same name). They're checked automatically — click Continue once they show as live.";

        _recheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _recheckTimer.Tick += async (_, _) => await CheckAllAsync(quiet: true);

        Closed += (_, _) => { _recheckTimer.Stop(); _verifier.Dispose(); };

        _recheckTimer.Start();
        _ = CheckAllAsync(); // initial check (continuations run on the dialog's dispatcher while modal)
    }

    private async Task CheckAllAsync(bool quiet = false)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            if (!quiet)
            {
                foreach (var vm in _records) { vm.CheckText = "Checking DNS…"; vm.CheckBrush = Brushes.Gray; }
                ReadyHint.Text = string.Empty;
            }

            foreach (var vm in _records)
            {
                try
                {
                    var exists = await _verifier.ExistsAsync(vm.Name, vm.Value);
                    vm.InDns = exists;
                    vm.CheckText = exists ? "✓ Already in DNS — no action needed" : "Not found in DNS yet";
                    vm.CheckBrush = exists ? Brushes.SeaGreen : Brushes.DarkOrange;
                }
                catch
                {
                    vm.InDns = false;
                    vm.CheckText = "Couldn't check DNS (offline?)";
                    vm.CheckBrush = Brushes.Gray;
                }
            }

            var allReady = _records.Count > 0 && _records.All(r => r.InDns);
            ReadyHint.Text = allReady
                ? "✓ All records are live in DNS — you can continue."
                : "Auto-rechecking DNS every 15s…";
            ReadyHint.Foreground = allReady ? Brushes.SeaGreen : Brushes.Gray;
            if (allReady) _recheckTimer.Stop();
        }
        finally
        {
            _checking = false;
        }
    }

    private void OnRecheck(object sender, RoutedEventArgs e)
    {
        _recheckTimer.Start(); // resume periodic checks if they were stopped
        _ = CheckAllAsync();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string text) return;

        try { Clipboard.SetText(text); }
        catch { /* clipboard momentarily unavailable */ }

        var original = button.Content;
        button.Content = "Copied!";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) => { button.Content = original; timer.Stop(); };
        timer.Start();
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
