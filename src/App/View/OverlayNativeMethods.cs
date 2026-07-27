using System.Runtime.InteropServices;

namespace RatScanner.View;

internal static class OverlayNativeMethods
{
    internal const int GwlExStyle = -20;

    internal const nint WsExTransparent = 0x00000020;
    internal const nint WsExToolWindow = 0x00000080;
    internal const nint WsExLayered = 0x00080000;
    internal const nint WsExNoActivate = 0x08000000;

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpFrameChangedFlags = SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged;

    internal const nint PassiveClickThroughStyles = WsExToolWindow | WsExTransparent | WsExLayered | WsExNoActivate;

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    internal static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    internal static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);
}
