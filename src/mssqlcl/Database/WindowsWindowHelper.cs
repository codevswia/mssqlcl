using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MssqlCl.Database;

/// <summary>
/// Provides a valid parent HWND on Windows for WAM (ActiveDirectoryInteractive) prompts,
/// including headless/stdio processes such as MCP servers that have no console window.
/// </summary>
internal static class WindowsWindowHelper
{
    private const uint GaRootOwner = 3;
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private static readonly Lock SyncRoot = new();
    private static IntPtr _hiddenWindowHandle;

    public static IntPtr GetParentWindowHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        return GetWindowsParentHandle();
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr GetWindowsParentHandle()
    {
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero)
        {
            var rootOwner = GetAncestor(consoleWindow, GaRootOwner);
            return rootOwner != IntPtr.Zero ? rootOwner : consoleWindow;
        }

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            return foreground;
        }

        return GetOrCreateHiddenOwnerWindow();
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr GetOrCreateHiddenOwnerWindow()
    {
        if (_hiddenWindowHandle != IntPtr.Zero)
        {
            return _hiddenWindowHandle;
        }

        lock (SyncRoot)
        {
            if (_hiddenWindowHandle != IntPtr.Zero)
            {
                return _hiddenWindowHandle;
            }

            using var ready = new ManualResetEventSlim(false);
            IntPtr createdHandle = IntPtr.Zero;

            var thread = new Thread(() =>
            {
                var centerX = Math.Max(0, GetSystemMetrics(0) / 2);
                var centerY = Math.Max(0, GetSystemMetrics(1) / 2);

                createdHandle = CreateWindowExW(
                    WsExToolWindow | WsExTopMost,
                    "STATIC",
                    "mssqlcl-auth-owner",
                    WsPopup,
                    centerX, centerY, 1, 1,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                ready.Set();

                if (createdHandle == IntPtr.Zero)
                {
                    return;
                }

                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            })
            {
                IsBackground = true,
                Name = "mssqlcl-wam-owner"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));

            _hiddenWindowHandle = createdHandle != IntPtr.Zero ? createdHandle : GetDesktopWindow();
            return _hiddenWindowHandle;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);
}
