using System;
using LetsSSL.Core.Iis;

namespace LetsSSL.App.ViewModels;

/// <summary>A checkable IIS site row for the multi-select site dropdown.</summary>
public sealed class SiteSelectionViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    public SiteSelectionViewModel(IisSiteInfo site, Action onChanged)
    {
        Site = site;
        _onChanged = onChanged;
    }

    public IisSiteInfo Site { get; }
    public string Name => Site.Name;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetField(ref _isSelected, value)) _onChanged(); }
    }
}
