namespace FinanceApp.Blazor.Components.Services;

/// <summary>
/// Notifies the sidebar that the set of market indices shown in navigation has changed.
/// </summary>
public sealed class MarketIndexNavigationService
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
