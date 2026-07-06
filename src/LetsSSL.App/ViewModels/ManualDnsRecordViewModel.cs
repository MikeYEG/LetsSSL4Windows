using System.Windows.Media;
using LetsSSL.Core.Dns;

namespace LetsSSL.App.ViewModels;

/// <summary>A manual DNS record row with a live "is it in DNS yet?" status.</summary>
public sealed class ManualDnsRecordViewModel : ViewModelBase
{
    public ManualDnsRecordViewModel(DnsTxtRecord record) => Record = record;

    public DnsTxtRecord Record { get; }
    public string Domain => Record.Domain;
    public string Name => Record.Name;
    public string Value => Record.Value;
    public string Heading => Record.Heading;

    private string _checkText = "Checking DNS…";
    public string CheckText { get => _checkText; set => SetField(ref _checkText, value); }

    private Brush _checkBrush = Brushes.Gray;
    public Brush CheckBrush { get => _checkBrush; set => SetField(ref _checkBrush, value); }

    /// <summary>True when the record's expected value was found in public DNS.</summary>
    public bool InDns { get; set; }
}
