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
    private const int RecheckSeconds = 15;

    private readonly List<ManualDnsRecordViewModel> _records;
    private readonly DnsTxtVerifier _verifier = new();
    private readonly DispatcherTimer _countdownTimer;
    private int _secondsLeft = RecheckSeconds;
    private bool _checking;
    private bool _allReady;

    public ManualDnsWindow(IReadOnlyList<DnsTxtRecord> records)
    {
        InitializeComponent();

        _records = records.Select(r => new ManualDnsRecordViewModel(r)).ToList();
        RecordsList.ItemsSource = _records;

        IntroText.Text = records.Count == 1
            ? "Create the following TXT record at your DNS provider. It's checked automatically — click Continue once it shows as live."
            : $"Create the following {records.Count} TXT records at your DNS provider (a wildcard plus its base domain " +
              "need two records with the same name). They're checked automatically — click Continue once they show as live.";

        // A 1-second tick drives a visible countdown; a full DNS re-check runs each
        // time it reaches zero (and once immediately on open).
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += OnCountdownTick;

        Closed += (_, _) => { _countdownTimer.Stop(); _verifier.Dispose(); };

        UpdateCountdownHint();
        _countdownTimer.Start();
        _ = CheckAllAsync(); // initial check (continuations run on the dialog's dispatcher while modal)
    }

    private async void OnCountdownTick(object? sender, EventArgs e)
    {
        if (_checking) return; // hold the countdown while a check is in flight
        _secondsLeft--;
        if (_secondsLeft <= 0)
        {
            await CheckAllAsync(quiet: true);
            _secondsLeft = RecheckSeconds;
        }
        if (!_allReady) UpdateCountdownHint();
    }

    private void UpdateCountdownHint()
    {
        ReadyHint.Text = $"Re-checking DNS in {_secondsLeft}s…";
        ReadyHint.Foreground = Brushes.Gray;
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
                ReadyHint.Text = "Checking DNS…";
                ReadyHint.Foreground = Brushes.Gray;
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

            _allReady = _records.Count > 0 && _records.All(r => r.InDns);
            if (_allReady)
            {
                ReadyHint.Text = "✓ All records are live in DNS — you can continue.";
                ReadyHint.Foreground = Brushes.SeaGreen;
                _countdownTimer.Stop();
            }
            else
            {
                _secondsLeft = RecheckSeconds;
                UpdateCountdownHint();
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private void OnRecheck(object sender, RoutedEventArgs e)
    {
        _secondsLeft = RecheckSeconds;
        if (!_allReady) _countdownTimer.Start(); // resume the countdown if it had stopped
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
