using System.Collections.ObjectModel;
using LetsSSL.Core.Iis;
using LetsSSL.Core.Models;
using LetsSSL.Core.Storage;

namespace LetsSSL.App.ViewModels;

/// <summary>Backs the New Certificate dialog (validation method, DNS, deployment).</summary>
public class NewCertificateViewModel : ViewModelBase
{
    public NewCertificateViewModel(string defaultEmail)
    {
        _contactEmail = defaultEmail;
        DnsProviders = new ObservableCollection<DnsProviderType>(
            Enum.GetValues<DnsProviderType>());
        _selectedDnsProvider = DnsProviderType.Manual;
        LoadIisSites();
    }

    // ---- Domains / contact ----

    public ObservableCollection<IisSiteInfo> IisSites { get; } = new();
    public bool IisAvailable { get; private set; }

    private IisSiteInfo? _selectedSite;
    public IisSiteInfo? SelectedSite
    {
        get => _selectedSite;
        set
        {
            if (SetField(ref _selectedSite, value) && value?.PhysicalPath is { } p)
            {
                if (string.IsNullOrWhiteSpace(WebRootPath)) WebRootPath = p;
                if (string.IsNullOrWhiteSpace(PrimaryDomain))
                {
                    var host = value.Bindings.Select(ParseHost).FirstOrDefault(h => !string.IsNullOrEmpty(h));
                    if (!string.IsNullOrEmpty(host)) PrimaryDomain = host!;
                }
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    private string _primaryDomain = string.Empty;
    public string PrimaryDomain
    {
        get => _primaryDomain;
        set { if (SetField(ref _primaryDomain, value)) OnPropertyChanged(nameof(CanSubmit)); }
    }

    private string _sansText = string.Empty;
    public string SansText { get => _sansText; set => SetField(ref _sansText, value); }

    private bool _isWildcard;
    /// <summary>
    /// When checked, prepends "*." to the primary domain and adds the bare apex
    /// domain as a SAN (so the cert covers both *.example.com and example.com),
    /// and forces DNS-01 (the only validation method a CA allows for wildcards).
    /// </summary>
    public bool IsWildcard
    {
        get => _isWildcard;
        set
        {
            if (!SetField(ref _isWildcard, value)) return;
            if (value)
            {
                UseDns = true; // wildcards must validate via DNS-01
                var apex = (PrimaryDomain ?? string.Empty).Trim().TrimStart('*').TrimStart('.');
                if (apex.Length > 0)
                {
                    PrimaryDomain = "*." + apex;
                    AddSan(apex); // also cover the bare domain
                }
            }
            else if ((PrimaryDomain ?? string.Empty).StartsWith("*.", StringComparison.Ordinal))
            {
                var apex = PrimaryDomain!.Substring(2);
                PrimaryDomain = apex;
                RemoveSan(apex);
            }
        }
    }

    private void AddSan(string domain)
    {
        var lines = SplitSans();
        if (!lines.Any(l => l.Equals(domain, StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add(domain);
            SansText = string.Join(Environment.NewLine, lines);
        }
    }

    private void RemoveSan(string domain)
    {
        var lines = SplitSans();
        if (lines.RemoveAll(l => l.Equals(domain, StringComparison.OrdinalIgnoreCase)) > 0)
            SansText = string.Join(Environment.NewLine, lines);
    }

    private List<string> SplitSans() =>
        SansText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private string _contactEmail;
    public string ContactEmail
    {
        get => _contactEmail;
        set { if (SetField(ref _contactEmail, value)) OnPropertyChanged(nameof(CanSubmit)); }
    }

    // ---- Validation method ----

    private bool _useDns;
    /// <summary>False = HTTP-01, True = DNS-01.</summary>
    public bool UseDns
    {
        get => _useDns;
        set
        {
            if (SetField(ref _useDns, value))
            {
                OnPropertyChanged(nameof(UseHttp));
                OnPropertyChanged(nameof(ShowDnsOptions));
                OnPropertyChanged(nameof(ShowHttpOptions));
                OnPropertyChanged(nameof(ShowCloudflareToken));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    /// <summary>Two-way helper for the HTTP radio button (inverse of <see cref="UseDns"/>).</summary>
    public bool UseHttp { get => !_useDns; set => UseDns = !value; }

    public ObservableCollection<DnsProviderType> DnsProviders { get; }

    private DnsProviderType _selectedDnsProvider;
    public DnsProviderType SelectedDnsProvider
    {
        get => _selectedDnsProvider;
        set
        {
            if (SetField(ref _selectedDnsProvider, value))
            {
                OnPropertyChanged(nameof(ShowCloudflareToken));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    private string _dnsApiToken = string.Empty;
    public string DnsApiToken
    {
        get => _dnsApiToken;
        set { if (SetField(ref _dnsApiToken, value)) OnPropertyChanged(nameof(CanSubmit)); }
    }

    public bool ShowHttpOptions => !UseDns;
    public bool ShowDnsOptions => UseDns;
    public bool ShowCloudflareToken => UseDns && SelectedDnsProvider == DnsProviderType.Cloudflare;

    // ---- HTTP web root ----

    private string _webRootPath = string.Empty;
    public string WebRootPath
    {
        get => _webRootPath;
        set { if (SetField(ref _webRootPath, value)) OnPropertyChanged(nameof(CanSubmit)); }
    }

    // ---- IIS binding ----

    private bool _bindToIis = true;
    public bool BindToIis { get => _bindToIis; set => SetField(ref _bindToIis, value); }

    private bool _autoRenew = true;
    public bool AutoRenew { get => _autoRenew; set => SetField(ref _autoRenew, value); }

    // ---- Deployment tasks (pragmatic subset; the model supports arbitrary lists) ----

    private bool _exportPfxEnabled;
    public bool ExportPfxEnabled { get => _exportPfxEnabled; set => SetField(ref _exportPfxEnabled, value); }

    private string _exportPfxPath = string.Empty;
    public string ExportPfxPath { get => _exportPfxPath; set => SetField(ref _exportPfxPath, value); }

    private string _exportPfxPassword = string.Empty;
    public string ExportPfxPassword { get => _exportPfxPassword; set => SetField(ref _exportPfxPassword, value); }

    private bool _runScriptEnabled;
    public bool RunScriptEnabled { get => _runScriptEnabled; set => SetField(ref _runScriptEnabled, value); }

    private string _runScriptPath = string.Empty;
    public string RunScriptPath { get => _runScriptPath; set => SetField(ref _runScriptPath, value); }

    private string _runScriptArguments = string.Empty;
    public string RunScriptArguments { get => _runScriptArguments; set => SetField(ref _runScriptArguments, value); }

    public bool CanSubmit
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PrimaryDomain) || string.IsNullOrWhiteSpace(ContactEmail))
                return false;
            if (UseDns)
                return SelectedDnsProvider != DnsProviderType.Cloudflare || !string.IsNullOrWhiteSpace(DnsApiToken);
            // HTTP-01 needs a place to write challenge files.
            return SelectedSite != null || !string.IsNullOrWhiteSpace(WebRootPath);
        }
    }

    private void LoadIisSites()
    {
        try
        {
            IisAvailable = IisManager.IsIisAvailable();
            if (!IisAvailable) return;
            foreach (var s in new IisManager().GetSites()) IisSites.Add(s);
        }
        catch { IisAvailable = false; }
    }

    public ManagedCertificate BuildConfig()
    {
        var sans = SansText
            .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        var cert = new ManagedCertificate
        {
            Name = PrimaryDomain.Trim(),
            PrimaryDomain = PrimaryDomain.Trim(),
            SubjectAlternativeNames = sans,
            ContactEmail = ContactEmail.Trim(),
            ChallengeType = UseDns ? ChallengeType.Dns01 : ChallengeType.Http01,
            IisSiteName = SelectedSite?.Name,
            WebRootPath = string.IsNullOrWhiteSpace(WebRootPath) ? null : WebRootPath.Trim(),
            BindToIis = BindToIis && SelectedSite != null,
            AutoRenew = AutoRenew,
        };

        if (UseDns)
        {
            cert.DnsProvider = SelectedDnsProvider;
            if (SelectedDnsProvider == DnsProviderType.Cloudflare && !string.IsNullOrWhiteSpace(DnsApiToken))
                cert.DnsCredentialProtected = SecretProtector.Protect(DnsApiToken.Trim());
        }

        if (ExportPfxEnabled && !string.IsNullOrWhiteSpace(ExportPfxPath))
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Path"] = ExportPfxPath.Trim() };
            if (!string.IsNullOrWhiteSpace(ExportPfxPassword)) settings["Password"] = ExportPfxPassword;
            cert.DeploymentTasks.Add(new DeploymentTaskConfig { Type = DeploymentTaskType.ExportPfx, Settings = settings });
        }

        if (RunScriptEnabled && !string.IsNullOrWhiteSpace(RunScriptPath))
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Path"] = RunScriptPath.Trim() };
            if (!string.IsNullOrWhiteSpace(RunScriptArguments)) settings["Arguments"] = RunScriptArguments.Trim();
            cert.DeploymentTasks.Add(new DeploymentTaskConfig { Type = DeploymentTaskType.RunScript, Settings = settings });
        }

        return cert;
    }

    private static string? ParseHost(string binding)
    {
        var parts = binding.Split(':');
        return parts.Length >= 3 ? parts[^1].Trim('/') : null;
    }
}
