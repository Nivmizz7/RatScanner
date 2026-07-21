using System;
using System.ComponentModel;

namespace RatScanner;

/// <summary>
/// Shared application-state service that separates three concepts that were previously
/// conflated in the UI: the persistent desktop sidebar (expanded/rail), the temporary
/// narrow overlay drawer, and the WPF minimal-overlay window mode.
/// </summary>
public sealed class AppStateService : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isNarrow;
    private bool _desktopSidebarOpen = true;
    private bool _drawerOpen;

    /// <summary>
    /// Whether the viewport is currently below the narrow breakpoint. The setter
    /// automatically closes the temporary narrow drawer.
    /// </summary>
    public bool IsNarrow
    {
        get => _isNarrow;
        set
        {
            if (_isNarrow == value)
                return;

            _isNarrow = value;

            // The persistent desktop sidebar/rail is replaced by the temporary
            // overlay drawer in narrow mode; the drawer always starts closed.
            // Use the property setter so events fire if it actually changes, but
            // always raise SidebarOpenChanged afterwards because IsSidebarOpen
            // switches its backing field (DesktopSidebarOpen -> DrawerOpen) even
            // when DrawerOpen was already false.
            DrawerOpen = false;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNarrow)));
            RaiseSidebarOpenChanged();
        }
    }

    /// <summary>
    /// Persistent desktop sidebar mode: <c>true</c> = expanded sidebar,
    /// <c>false</c> = collapsed icon rail.
    /// </summary>
    public bool DesktopSidebarOpen
    {
        get => _desktopSidebarOpen;
        private set
        {
            if (_desktopSidebarOpen == value)
                return;

            _desktopSidebarOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DesktopSidebarOpen)));
            DesktopSidebarOpenChanged?.Invoke(this, value);
            RaiseSidebarOpenChanged();
        }
    }

    public event EventHandler<bool>? DesktopSidebarOpenChanged;

    /// <summary>
    /// Temporary narrow overlay drawer. Independent of the desktop expanded/rail state.
    /// </summary>
    public bool DrawerOpen
    {
        get => _drawerOpen;
        private set
        {
            if (_drawerOpen == value)
                return;

            _drawerOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DrawerOpen)));
            DrawerOpenChanged?.Invoke(this, value);
            RaiseSidebarOpenChanged();
        }
    }

    public event EventHandler<bool>? DrawerOpenChanged;

    /// <summary>
    /// Whether the navigation sidebar is visually open from the title-bar perspective
    /// (expanded desktop sidebar or open narrow drawer).
    /// </summary>
    public bool IsSidebarOpen => _isNarrow ? _drawerOpen : _desktopSidebarOpen;

    public event EventHandler<bool>? SidebarOpenChanged;

    /// <summary>Raised when the title-bar navigation toggle is activated.</summary>
    public event EventHandler? SidebarToggleRequested;

    /// <summary>Raised when focus should return to the title-bar navigation toggle.</summary>
    public event EventHandler? FocusNavigationToggleRequested;

    public void ToggleSidebar() => SidebarToggleRequested?.Invoke(this, EventArgs.Empty);

    public void SetDesktopSidebarOpen(bool open) => DesktopSidebarOpen = open;

    public void SetDrawerOpen(bool open) => DrawerOpen = open;

    public void RequestFocusNavigationToggle() => FocusNavigationToggleRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseSidebarOpenChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSidebarOpen)));
        SidebarOpenChanged?.Invoke(this, IsSidebarOpen);
    }
}
