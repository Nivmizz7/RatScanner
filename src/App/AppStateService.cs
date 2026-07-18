using System;
using System.ComponentModel;

namespace RatScanner;

/// <summary>
/// Small shared application-state service that lets the WPF chrome and the
/// Blazor shell keep the navigation sidebar and focus in sync.
/// </summary>
public sealed class AppStateService : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the persistent sidebar open/closed state changes.</summary>
    public event EventHandler<bool>? SidebarOpenChanged;

    /// <summary>Raised when the Blazor overlay drawer wants focus back on the WPF toggle.</summary>
    public event EventHandler? FocusNavigationToggleRequested;

    private bool _sidebarOpen = true;

    public bool SidebarOpen
    {
        get => _sidebarOpen;
        private set
        {
            if (_sidebarOpen == value)
                return;

            _sidebarOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarOpen)));
            SidebarOpenChanged?.Invoke(this, value);
        }
    }

    public void ToggleSidebar() => SidebarOpen = !SidebarOpen;

    public void SetSidebarOpen(bool open) => SidebarOpen = open;

    public void RequestFocusNavigationToggle() => FocusNavigationToggleRequested?.Invoke(this, EventArgs.Empty);
}
