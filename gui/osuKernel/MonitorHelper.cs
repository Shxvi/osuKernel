using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace osuKernel
{
    public class DisplayInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsPrimary { get; set; }

        public override string ToString() =>
            $"{Name} ({Width}x{Height}{(IsPrimary ? " - Primary" : "")})";
    }

    public static class MonitorHelper
    {
        public static List<DisplayInfo> GetDisplays()
        {
            var list = new List<DisplayInfo>();
            int index = 1;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    var mi = new MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));

                    if (GetMonitorInfo(hMonitor, ref mi))
                    {
                        int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                        int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                        bool primary = (mi.dwFlags & 1) != 0;

                        list.Add(new DisplayInfo
                        {
                            Name = $"Display {index++}",
                            Left = mi.rcMonitor.Left,
                            Top = mi.rcMonitor.Top,
                            Width = w,
                            Height = h,
                            IsPrimary = primary
                        });
                    }
                    return true;
                }, IntPtr.Zero);

            // Add Virtual Desktop option (spans all monitors)
            int virtL = (int)SystemParameters.VirtualScreenLeft;
            int virtT = (int)SystemParameters.VirtualScreenTop;
            int virtW = (int)SystemParameters.VirtualScreenWidth;
            int virtH = (int)SystemParameters.VirtualScreenHeight;

            if (list.Count > 1)
            {
                list.Insert(0, new DisplayInfo
                {
                    Name = "All Displays (Virtual Desktop)",
                    Left = virtL,
                    Top = virtT,
                    Width = virtW,
                    Height = virtH,
                    IsPrimary = false
                });
            }

            if (list.Count == 0)
            {
                list.Add(new DisplayInfo
                {
                    Name = "Primary Display",
                    Left = 0,
                    Top = 0,
                    Width = 1920,
                    Height = 1080,
                    IsPrimary = true
                });
            }

            return list;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }
    }
}
