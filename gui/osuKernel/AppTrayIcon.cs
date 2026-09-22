using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace osuKernel
{
    public class AppTrayIcon : IDisposable
    {
        private const int WM_USER = 0x0400;
        public const int WM_TRAYICON = WM_USER + 101;
        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;

        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly Window _window;
        private IntPtr _hwnd;
        private HwndSource? _hwndSource;
        private bool _isAdded;
        private readonly ContextMenu _contextMenu;

        public event Action? DoubleClick;

        public AppTrayIcon(Window window, string tooltip)
        {
            _window = window;
            _contextMenu = new ContextMenu();

            var itemOpen = new MenuItem { Header = "Open Configurator" };
            itemOpen.Click += (s, e) => DoubleClick?.Invoke();
            _contextMenu.Items.Add(itemOpen);

            var itemReset = new MenuItem { Header = "Reset Area to Full" };
            itemReset.Click += (s, e) =>
            {
                if (_window is MainWindow mw)
                {
                    mw.ResetAreaToFull();
                }
            };
            _contextMenu.Items.Add(itemReset);

            _contextMenu.Items.Add(new Separator());

            var itemExit = new MenuItem { Header = "Exit CTL-472" };
            itemExit.Click += (s, e) =>
            {
                if (_window is MainWindow mw)
                {
                    mw.ExitApplication();
                }
            };
            _contextMenu.Items.Add(itemExit);

            if (_window.IsLoaded)
            {
                HookWindow(tooltip);
            }
            else
            {
                _window.Loaded += (s, e) => HookWindow(tooltip);
            }
        }

        private void HookWindow(string tooltip)
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            if (_hwnd == IntPtr.Zero) return;

            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);

            // 32512 = IDI_APPLICATION
            IntPtr hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512);

            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = hIcon,
                szTip = tooltip
            };

            _isAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int evt = lParam.ToInt32();
                if (evt == WM_LBUTTONDBLCLK)
                {
                    DoubleClick?.Invoke();
                    handled = true;
                }
                else if (evt == WM_RBUTTONUP)
                {
                    SetForegroundWindow(_hwnd);
                    _contextMenu.Placement = PlacementMode.MousePoint;
                    _contextMenu.IsOpen = true;
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_isAdded && _hwnd != IntPtr.Zero)
            {
                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 1001
                };
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _isAdded = false;
            }

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
        }
    }
}
