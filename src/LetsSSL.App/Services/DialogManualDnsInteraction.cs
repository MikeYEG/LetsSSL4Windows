using System.Collections.Generic;
using System.Linq;
using System.Windows;
using LetsSSL.App.Views;
using LetsSSL.Core.Dns;

namespace LetsSSL.App.Services;

/// <summary>
/// Implements manual DNS validation in the GUI: shows a single dialog listing the
/// TXT records to create, and reports the post-validation cleanup notice to the
/// activity log (rather than another popup).
/// </summary>
public sealed class DialogManualDnsInteraction : IManualDnsInteraction
{
    /// <summary>Sink for the cleanup notice. Set by the main window to the activity log.</summary>
    public IProgress<string>? Log { get; set; }

    public Task PromptCreateAsync(IReadOnlyList<DnsTxtRecord> records, CancellationToken ct = default)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new ManualDnsWindow(records) { Owner = Application.Current.MainWindow };
            window.ShowDialog();
        }).Task;
    }

    public Task PromptRemoveAsync(IReadOnlyList<DnsTxtRecord> records, CancellationToken ct = default)
    {
        var names = string.Join(", ", records.Select(r => r.Name).Distinct());
        Log?.Report($"DNS validation complete — you can now remove the TXT record(s): {names}");
        return Task.CompletedTask;
    }
}
