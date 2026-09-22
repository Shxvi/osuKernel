using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace osuKernel
{
    public class TabletSpecification
    {
        public string ModelId { get; set; } = "CTL-472";
        public string DisplayName { get; set; } = "One by Wacom Small (CTL-472)";
        public string Series { get; set; } = "One by Wacom";
        public double WidthMm { get; set; } = 152.0;
        public double HeightMm { get; set; } = 95.0;
        public int MaxX { get; set; } = 15200;
        public int MaxY { get; set; } = 9500;
        public double CountsPerMm { get; set; } = 100.0;
        public string[] SupportedPids { get; set; } = Array.Empty<string>();

        public override string ToString() => DisplayName;
    }

    public static class TabletDatabase
    {
        public static readonly TabletSpecification DefaultTablet = new()
        {
            ModelId = "CTL-472",
            DisplayName = "One by Wacom Small (CTL-472)",
            Series = "One by Wacom",
            WidthMm = 152.0,
            HeightMm = 95.0,
            MaxX = 15200,
            MaxY = 9500,
            CountsPerMm = 100.0,
            SupportedPids = new[] { "037A" }
        };

        public static readonly IReadOnlyList<TabletSpecification> AllTablets = new List<TabletSpecification>
        {
            DefaultTablet,
            new()
            {
                ModelId = "CTL-672",
                DisplayName = "One by Wacom Medium (CTL-672)",
                Series = "One by Wacom",
                WidthMm = 216.0,
                HeightMm = 135.0,
                MaxX = 21600,
                MaxY = 13500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "037B" }
            },
            new()
            {
                ModelId = "CTL-480",
                DisplayName = "Wacom Intuos Small (CTL-480 / CTH-480)",
                Series = "Intuos 2013",
                WidthMm = 152.0,
                HeightMm = 95.0,
                MaxX = 15200,
                MaxY = 9500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "030E", "030F" }
            },
            new()
            {
                ModelId = "CTL-680",
                DisplayName = "Wacom Intuos Medium (CTL-680 / CTH-680)",
                Series = "Intuos 2013",
                WidthMm = 216.0,
                HeightMm = 135.0,
                MaxX = 21600,
                MaxY = 13500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "0310", "0311" }
            },
            new()
            {
                ModelId = "CTL-470",
                DisplayName = "Wacom Bamboo Pen & Touch (CTL-470 / CTH-470)",
                Series = "Bamboo Gen 3",
                WidthMm = 147.2,
                HeightMm = 92.0,
                MaxX = 14720,
                MaxY = 9200,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "00DD", "00DE" }
            },
            new()
            {
                ModelId = "CTH-670",
                DisplayName = "Wacom Bamboo Fun Medium (CTH-670)",
                Series = "Bamboo Gen 3",
                WidthMm = 216.48,
                HeightMm = 137.0,
                MaxX = 21648,
                MaxY = 13700,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "00DF" }
            },
            new()
            {
                ModelId = "CTL-471",
                DisplayName = "Wacom Bamboo One Small (CTL-471)",
                Series = "Bamboo One",
                WidthMm = 152.0,
                HeightMm = 95.0,
                MaxX = 15200,
                MaxY = 9500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "0300" }
            },
            new()
            {
                ModelId = "CTL-671",
                DisplayName = "Wacom Bamboo One Medium (CTL-671)",
                Series = "Bamboo One",
                WidthMm = 216.0,
                HeightMm = 135.0,
                MaxX = 21600,
                MaxY = 13500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "0301" }
            },
            new()
            {
                ModelId = "CTL-490",
                DisplayName = "Wacom Intuos 2015 Small (CTL-490 / CTH-490)",
                Series = "Intuos 2015",
                WidthMm = 152.0,
                HeightMm = 95.0,
                MaxX = 15200,
                MaxY = 9500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "033B", "033C" }
            },
            new()
            {
                ModelId = "CTL-690",
                DisplayName = "Wacom Intuos 2015 Medium (CTL-690 / CTH-690)",
                Series = "Intuos 2015",
                WidthMm = 216.0,
                HeightMm = 135.0,
                MaxX = 21600,
                MaxY = 13500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "033D", "033E" }
            },
            new()
            {
                ModelId = "CTL-4100",
                DisplayName = "Wacom Intuos 2018 Small (CTL-4100 / WL)",
                Series = "Intuos 2018",
                WidthMm = 152.0,
                HeightMm = 95.0,
                MaxX = 15200,
                MaxY = 9500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "0374", "0375" }
            },
            new()
            {
                ModelId = "CTL-6100",
                DisplayName = "Wacom Intuos 2018 Medium (CTL-6100 / WL)",
                Series = "Intuos 2018",
                WidthMm = 216.0,
                HeightMm = 135.0,
                MaxX = 21600,
                MaxY = 13500,
                CountsPerMm = 100.0,
                SupportedPids = new[] { "0377", "0378" }
            }
        };

        public static TabletSpecification GetByModelId(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return DefaultTablet;
            return AllTablets.FirstOrDefault(t => string.Equals(t.ModelId, modelId, StringComparison.OrdinalIgnoreCase)) ?? DefaultTablet;
        }

        public static TabletSpecification? GetByPid(string? pidHex)
        {
            if (string.IsNullOrWhiteSpace(pidHex)) return null;
            string cleanPid = pidHex.Trim().ToUpperInvariant();
            return AllTablets.FirstOrDefault(t => t.SupportedPids.Any(p => string.Equals(p, cleanPid, StringComparison.OrdinalIgnoreCase)));
        }

        public static TabletSpecification DetectConnectedTablet()
        {
            try
            {
                // 1. Try SetupAPI for currently connected (present) USB devices
                var presentTablet = DetectViaSetupApi();
                if (presentTablet != null) return presentTablet;
            }
            catch { }

            try
            {
                // 2. Fallback to scanning registry USB enum
                var regTablet = DetectViaRegistry();
                if (regTablet != null) return regTablet;
            }
            catch { }

            return DefaultTablet;
        }

        private static TabletSpecification? DetectViaSetupApi()
        {
            const uint DIGCF_PRESENT = 0x00000002;
            const uint DIGCF_ALLCLASSES = 0x00000004;

            IntPtr devInfo = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (devInfo == IntPtr.Zero || devInfo == (IntPtr)(-1))
            {
                return null;
            }

            try
            {
                SP_DEVINFO_DATA devData = new SP_DEVINFO_DATA();
                devData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();

                uint index = 0;
                while (SetupDiEnumDeviceInfo(devInfo, index++, ref devData))
                {
                    StringBuilder sb = new StringBuilder(512);
                    if (SetupDiGetDeviceInstanceId(devInfo, ref devData, sb, sb.Capacity, out _))
                    {
                        string instanceId = sb.ToString().ToUpperInvariant();
                        if (instanceId.Contains("VID_056A"))
                        {
                            foreach (var tablet in AllTablets)
                            {
                                foreach (var pid in tablet.SupportedPids)
                                {
                                    if (instanceId.Contains($"PID_{pid}"))
                                    {
                                        return tablet;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }

            return null;
        }

        private static TabletSpecification? DetectViaRegistry()
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey == null) return null;

            foreach (var subKeyName in usbKey.GetSubKeyNames())
            {
                string upperName = subKeyName.ToUpperInvariant();
                if (upperName.StartsWith("VID_056A&PID_"))
                {
                    foreach (var tablet in AllTablets)
                    {
                        foreach (var pid in tablet.SupportedPids)
                        {
                            if (upperName.Contains($"PID_{pid}"))
                            {
                                return tablet;
                            }
                        }
                    }
                }
            }

            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }
}
