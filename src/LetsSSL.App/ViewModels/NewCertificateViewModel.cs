using System.Collections.ObjectModel;
using System.Text.Json;
using LetsSSL.Core.Dns;
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

    /// <summary>Checkable IIS sites; more than one can be selected for binding.</summary>
    public ObservableCollection<SiteSelectionViewModel> SiteSelections { get; } = new();
    public bool IisAvailable { get; private set; }

    public IEnumerable<IisSiteInfo> SelectedSites =>
        SiteSelections.Where(s => s.IsSelected).Select(s => s.Site);

    /// <summary>Text shown on the collapsed multi-select dropdown.</summary>
    public string SitesSummary
    {
        get
        {
            var names = SelectedSites.Select(s => s.Name).ToList();
            return names.Count switch
            {
                0 => "Select IIS site(s)…",
                1 => names[0],
                2 => $"{names[0]}, {names[1]}",
                _ => $"{names.Count} sites selected",
            };
        }
    }

    private void OnSiteSelectionChanged()
    {
        var first = SelectedSites.FirstOrDefault();
        if (first is not null)
        {
            if (string.IsNullOrWhiteSpace(WebRootPath) && first.PhysicalPath is { } p)
                WebRootPath = p;
            if (string.IsNullOrWhiteSpace(PrimaryDomain))
            {
                var host = first.Bindings.Select(ParseHost).FirstOrDefault(h => !string.IsNullOrEmpty(h));
                if (!string.IsNullOrEmpty(host)) PrimaryDomain = host!;
            }
        }
        OnPropertyChanged(nameof(SitesSummary));
        OnPropertyChanged(nameof(CanSubmit));
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
    /// When checked, prepends "*." to the primary domain and forces DNS-01 (the
    /// only validation method a CA allows for wildcards). The bare apex domain is
    /// covered too only when <see cref="IncludeApexDomain"/> is set.
    /// </summary>
    public bool IsWildcard
    {
        get => _isWildcard;
        set
        {
            if (!SetField(ref _isWildcard, value)) return;
            OnPropertyChanged(nameof(ShowApexOption));
            if (value)
            {
                UseDns = true; // wildcards must validate via DNS-01
                var apex = ApexOf(PrimaryDomain);
                if (apex.Length > 0)
                {
                    PrimaryDomain = "*." + apex;
                    if (IncludeApexDomain) AddSan(apex); // optionally also cover the bare domain
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

    private bool _includeApexDomain = true;
    /// <summary>
    /// For a wildcard cert, also cover the bare apex domain (example.com). This
    /// adds a SECOND DNS-01 TXT record (the wildcard and the apex validate
    /// separately at the same _acme-challenge name). Uncheck to need only one
    /// record — the cert then covers *.example.com but not example.com itself.
    /// </summary>
    public bool IncludeApexDomain
    {
        get => _includeApexDomain;
        set
        {
            if (!SetField(ref _includeApexDomain, value)) return;
            if (!IsWildcard) return;
            var apex = ApexOf(PrimaryDomain);
            if (apex.Length == 0) return;
            if (value) AddSan(apex); else RemoveSan(apex);
        }
    }

    /// <summary>Whether to show the "also cover the bare domain" option (wildcard only).</summary>
    public bool ShowApexOption => IsWildcard;

    /// <summary>The bare apex domain for a primary domain, stripping any leading "*.".</summary>
    private static string ApexOf(string? domain) =>
        (domain ?? string.Empty).Trim().TrimStart('*').TrimStart('.');

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
                OnPropertyChanged(nameof(ShowRoute53Fields));
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
                OnPropertyChanged(nameof(ShowRoute53Fields));
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

    // ---- Route 53 (AWS) credentials ----

    private string _awsAccessKeyId = string.Empty;
    public string AwsAccessKeyId
    {
        get => _awsAccessKeyId;
        set { if (SetField(ref _awsAccessKeyId, value)) OnPropertyChanged(nameof(CanSubmit)); }
    }

    private string _awsSecretAccessKey = string.Empty;
    public string AwsSecretAccessKey
    {
        get => _awsSecretAccessKey;
        set { if (SetField(ref _awsSecretAccessKey, value)) OnPropertyChanged(nameof(CanSubmit)); }
    }

    private string _awsHostedZoneId = string.Empty;
    /// <summary>Optional; auto-discovered from the domain when left blank.</summary>
    public string AwsHostedZoneId { get => _awsHostedZoneId; set => SetField(ref _awsHostedZoneId, value); }

    public bool ShowHttpOptions => !UseDns;
    public bool ShowDnsOptions => UseDns;
    public bool ShowCloudflareToken => UseDns && SelectedDnsProvider == DnsProviderType.Cloudflare;
    public bool ShowRoute53Fields => UseDns && SelectedDnsProvider == DnsProviderType.Route53;

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

    /// <summary>
    /// Optional friendly name for the certificate in the Windows store, shown in
    /// IIS's Server Certificates list. Blank leaves it unset.
    /// </summary>
    private string _friendlyName = string.Empty;
    public string FriendlyName { get => _friendlyName; set => SetField(ref _friendlyName, value); }

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
                return SelectedDnsProvider switch
                {
                    DnsProviderType.Cloudflare => !string.IsNullOrWhiteSpace(DnsApiToken),
                    DnsProviderType.Route53 => !string.IsNullOrWhiteSpace(AwsAccessKeyId)
                                               && !string.IsNullOrWhiteSpace(AwsSecretAccessKey),
                    _ => true, // Manual needs nothing here
                };
            // HTTP-01 needs a place to write challenge files.
            return SelectedSites.Any() || !string.IsNullOrWhiteSpace(WebRootPath);
        }
    }

    private void LoadIisSites()
    {
        try
        {
            IisAvailable = IisManager.IsIisAvailable();
            if (!IisAvailable) return;
            foreach (var s in new IisManager().GetSites())
                SiteSelections.Add(new SiteSelectionViewModel(s, OnSiteSelectionChanged));
        }
        catch { IisAvailable = false; }
    }

    public ManagedCertificate BuildConfig()
    {
        var sans = SansText
            .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        var selectedSites = SelectedSites.ToList();

        var cert = new ManagedCertificate
        {
            Name = PrimaryDomain.Trim(),
            PrimaryDomain = PrimaryDomain.Trim(),
            SubjectAlternativeNames = sans,
            ContactEmail = ContactEmail.Trim(),
            ChallengeType = UseDns ? ChallengeType.Dns01 : ChallengeType.Http01,
            IisSiteName = selectedSites.FirstOrDefault()?.Name,
            IisSiteNames = selectedSites.Select(s => s.Name).ToList(),
            WebRootPath = string.IsNullOrWhiteSpace(WebRootPath) ? null : WebRootPath.Trim(),
            BindToIis = BindToIis && selectedSites.Count > 0,
            AutoRenew = AutoRenew,
            FriendlyName = string.IsNullOrWhiteSpace(FriendlyName) ? null : FriendlyName.Trim(),
        };

        if (UseDns)
        {
            cert.DnsProvider = SelectedDnsProvider;
            if (SelectedDnsProvider == DnsProviderType.Cloudflare && !string.IsNullOrWhiteSpace(DnsApiToken))
            {
                cert.DnsCredentialProtected = SecretProtector.Protect(DnsApiToken.Trim());
            }
            else if (SelectedDnsProvider == DnsProviderType.Route53 && !string.IsNullOrWhiteSpace(AwsAccessKeyId))
            {
                var creds = new Route53Credentials(
                    AwsAccessKeyId.Trim(),
                    AwsSecretAccessKey.Trim(),
                    string.IsNullOrWhiteSpace(AwsHostedZoneId) ? null : AwsHostedZoneId.Trim());
                cert.DnsCredentialProtected = SecretProtector.Protect(JsonSerializer.Serialize(creds));
            }
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
