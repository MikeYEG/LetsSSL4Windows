using LetsSSL.Core.Models;

namespace LetsSSL.App.ViewModels;

/// <summary>
/// Editable row for one remote IIS server (WinRM) in the New Certificate dialog.
/// </summary>
public class RemoteTargetViewModel : ViewModelBase
{
    public RemoteTargetViewModel() { }

    public RemoteTargetViewModel(RemoteIisTarget target)
    {
        _host = target.Host;
        _winRmPort = target.WinRmPort;
        _useSsl = target.UseSsl;
        _siteNamesText = string.Join(", ", target.SiteNames);
    }

    private string _host = string.Empty;
    public string Host { get => _host; set => SetField(ref _host, value); }

    private int _winRmPort = 5986;
    public int WinRmPort { get => _winRmPort; set => SetField(ref _winRmPort, value); }

    private bool _useSsl = true;
    public bool UseSsl { get => _useSsl; set => SetField(ref _useSsl, value); }

    /// <summary>Comma- or newline-separated remote IIS site names.</summary>
    private string _siteNamesText = string.Empty;
    public string SiteNamesText { get => _siteNamesText; set => SetField(ref _siteNamesText, value); }

    /// <summary>True once the row has a host name worth persisting.</summary>
    public bool HasHost => !string.IsNullOrWhiteSpace(Host);

    /// <summary>Builds the model, or null when the row has no host name.</summary>
    public RemoteIisTarget? ToModel()
    {
        if (!HasHost) return null;
        return new RemoteIisTarget
        {
            Host = Host.Trim(),
            WinRmPort = WinRmPort <= 0 ? 5986 : WinRmPort,
            UseSsl = UseSsl,
            SiteNames = _siteNamesText
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList(),
        };
    }
}
