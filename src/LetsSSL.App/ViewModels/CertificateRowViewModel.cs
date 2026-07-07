using System.Linq;
using System.Windows.Media;
using LetsSSL.Core.Models;

namespace LetsSSL.App.ViewModels;

/// <summary>Display wrapper around a <see cref="ManagedCertificate"/> for the grid.</summary>
public class CertificateRowViewModel : ViewModelBase
{
    public ManagedCertificate Model { get; }

    public CertificateRowViewModel(ManagedCertificate model) => Model = model;

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.PrimaryDomain : Model.Name;
    public string Domains => string.Join(", ", Model.AllDomains);
    public string IisSite =>
        Model.IisSiteNames is { Count: > 0 } names ? string.Join(", ", names)
        : Model.IisSiteName ?? "—";

    public string Expiry => Model.NotAfter is { } na
        ? na.ToLocalTime().ToString("yyyy-MM-dd")
        : "—";

    public string DaysLeft
    {
        get
        {
            if (Model.NotAfter is not { } na) return "—";
            var days = (na - DateTimeOffset.UtcNow).Days;
            return days < 0 ? "expired" : $"{days} days";
        }
    }

    public CertificateStatus Status => Model.GetStatus(DateTimeOffset.UtcNow);

    public string StatusText => Status switch
    {
        CertificateStatus.Valid => "Valid",
        CertificateStatus.ExpiringSoon => "Expiring soon",
        CertificateStatus.Expired => "Expired",
        CertificateStatus.Error => "Error",
        _ => "Not requested",
    };

    public Brush StatusBrush => Status switch
    {
        CertificateStatus.Valid => Brushes.SeaGreen,
        CertificateStatus.ExpiringSoon => Brushes.DarkOrange,
        CertificateStatus.Expired => Brushes.Firebrick,
        CertificateStatus.Error => Brushes.Firebrick,
        _ => Brushes.Gray,
    };

    public string? ToolTip => Model.LastError;

    public string? Thumbprint => Model.Thumbprint;
    public bool HasThumbprint => !string.IsNullOrEmpty(Model.Thumbprint);

    /// <summary>Re-raises bindings after the underlying model changes.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Domains));
        OnPropertyChanged(nameof(IisSite));
        OnPropertyChanged(nameof(Expiry));
        OnPropertyChanged(nameof(DaysLeft));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(Thumbprint));
        OnPropertyChanged(nameof(HasThumbprint));
    }
}
