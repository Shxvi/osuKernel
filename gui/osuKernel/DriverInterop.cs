using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace osuKernel
{
    public enum DriverConnectionState
    {
        Disconnected,
        KernelModeConnected,
        UserModeFallbackActive
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct OSUKERNEL_AREA_CONFIG
    {
        public int TabletInputX;
        public int TabletInputY;
        public uint TabletInputWidth;
        public uint TabletInputHeight;

        public int ScreenOutputX;
        public int ScreenOutputY;
        public uint ScreenOutputWidth;
        public uint ScreenOutputHeight;

        public uint AreaClipping;
        public uint AreaLimiting;
        public uint RotationAngle;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct OSUKERNEL_SETTINGS
    {
        public uint LowLatencyMode;
        public uint PressureCurveType;
        public uint PressureGammaFixed;
        public uint TipClickThreshold;
        public uint RawPassthrough;
        public uint EnableSmoothing;
        public uint SmoothingStrength;
        public uint AntichatterDeadzone;
        public uint InterpolationRate;
        public uint DisableTipClick;
        public uint EnablePressure;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct OSUKERNEL_DRIVER_STATS
    {
        public ulong TotalPackets;
        public uint PacketsPerSecond;

        public ushort LastRawX;
        public ushort LastRawY;
        public ushort LastRawPressure;
        public byte LastButtons;
        public byte InProximity;

        public int LastScreenX;
        public int LastScreenY;

        public ulong LastPacketTimestamp;
        public uint DroppedPackets;
    }

    public class DriverInterop : IDisposable
    {
        private static readonly string[] DevicePaths = new[]
        {
            @"\\.\osuKernelDevice",
            @"\\.\CTL472Device"
        };

        private const uint FILE_DEVICE_OSUKERNEL = 0x8056;
        private const uint METHOD_BUFFERED = 0;
        private const uint FILE_ANY_ACCESS = 0;

        private static uint CTL_CODE(uint deviceType, uint function, uint method, uint access) =>
            (deviceType << 16) | (access << 14) | (function << 2) | method;

        public static readonly uint IOCTL_OSUKERNEL_SET_AREA =
            CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS);

        public static readonly uint IOCTL_OSUKERNEL_GET_AREA =
            CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS);

        public static readonly uint IOCTL_OSUKERNEL_SET_CONFIG =
            CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS);

        public static readonly uint IOCTL_OSUKERNEL_GET_STATS =
            CTL_CODE(FILE_DEVICE_OSUKERNEL, 0x804, METHOD_BUFFERED, FILE_ANY_ACCESS);

        private SafeFileHandle? _deviceHandle;
        private bool _isDisposed;
        private readonly IntPtr _statsBuffer;
        private readonly int _statsBufferSize;
        private readonly object _ioLock = new();

        public DriverConnectionState State { get; private set; } = DriverConnectionState.Disconnected;

        // Cached stats for zero-allocation telemetry
        private OSUKERNEL_DRIVER_STATS _currentStats;
        private OSUKERNEL_DRIVER_STATS _simulatedStats;

        public DriverInterop()
        {
            _statsBufferSize = Marshal.SizeOf<OSUKERNEL_DRIVER_STATS>();
            _statsBuffer = Marshal.AllocHGlobal(_statsBufferSize);
        }

        public bool Connect()
        {
            lock (_ioLock)
            {
                try
                {
                    if (_deviceHandle != null && !_deviceHandle.IsInvalid && !_deviceHandle.IsClosed)
                    {
                        _deviceHandle.Dispose();
                        _deviceHandle = null;
                    }

                    foreach (var path in DevicePaths)
                    {
                        _deviceHandle = CreateFile(
                            path,
                            FileAccess.ReadWrite,
                            FileShare.ReadWrite,
                            IntPtr.Zero,
                            FileMode.Open,
                            FileAttributes.Normal,
                            IntPtr.Zero
                        );

                        if (_deviceHandle == null || _deviceHandle.IsInvalid)
                        {
                            // Fallback to 0 desired access (valid for FILE_ANY_ACCESS IOCTLs and non-elevated users)
                            _deviceHandle = CreateFile(
                                path,
                                0,
                                FileShare.ReadWrite,
                                IntPtr.Zero,
                                FileMode.Open,
                                FileAttributes.Normal,
                                IntPtr.Zero
                            );
                        }

                        if (_deviceHandle != null && !_deviceHandle.IsInvalid)
                        {
                            State = DriverConnectionState.KernelModeConnected;
                            return true;
                        }
                    }
                }
                catch
                {
                    // Fall through to fallback
                }

                // If kernel driver is not running or device not open, activate fallback mode
                State = DriverConnectionState.UserModeFallbackActive;
                return false;
            }
        }

        public bool CheckAndReconnect()
        {
            if (State == DriverConnectionState.KernelModeConnected && _deviceHandle != null && !_deviceHandle.IsInvalid)
            {
                return true;
            }
            return Connect();
        }

        public bool SendAreaConfig(TabletAreaModel model)
        {
            var config = new OSUKERNEL_AREA_CONFIG
            {
                TabletInputX = model.XCounts,
                TabletInputY = model.YCounts,
                TabletInputWidth = (uint)Math.Max(1, model.WidthCounts),
                TabletInputHeight = (uint)Math.Max(1, model.HeightCounts),
                ScreenOutputX = model.ScreenX,
                ScreenOutputY = model.ScreenY,
                ScreenOutputWidth = (uint)Math.Max(1, model.ScreenWidth),
                ScreenOutputHeight = (uint)Math.Max(1, model.ScreenHeight),
                AreaClipping = model.AreaClipping ? 1u : 0u,
                AreaLimiting = model.AreaLimiting ? 1u : 0u,
                RotationAngle = (uint)(((model.Rotation % 360) + 360) % 360),
                Reserved = new uint[4]
            };

            if (State == DriverConnectionState.KernelModeConnected && _deviceHandle != null && !_deviceHandle.IsInvalid)
            {
                int size = Marshal.SizeOf<OSUKERNEL_AREA_CONFIG>();
                IntPtr inBuffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(config, inBuffer, false);
                    lock (_ioLock)
                    {
                        bool success = DeviceIoControl(
                            _deviceHandle,
                            IOCTL_OSUKERNEL_SET_AREA,
                            inBuffer,
                            (uint)size,
                            IntPtr.Zero,
                            0,
                            out _,
                            IntPtr.Zero
                        );
                        return success;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }

            return true;
        }

        public bool SendSettings(TabletAreaModel model)
        {
            var settings = new OSUKERNEL_SETTINGS
            {
                LowLatencyMode = model.LowLatencyKernelMode ? 1u : 0u,
                PressureCurveType = 0,
                PressureGammaFixed = 65536u,
                TipClickThreshold = 0u,
                RawPassthrough = 0,
                EnableSmoothing = model.EnableSmoothing ? 1u : 0u,
                SmoothingStrength = model.DevMode ? (uint)Math.Max(0, model.SmoothingStrength) : (uint)Math.Clamp(model.SmoothingStrength, 0, 95),
                AntichatterDeadzone = model.DevMode ? (uint)Math.Max(0, model.AntichatterDeadzone) : (uint)Math.Clamp(model.AntichatterDeadzone, 0, 50),
                InterpolationRate = (uint)model.InterpolationRate,
                DisableTipClick = model.EnableTipClick ? 0u : 1u,
                EnablePressure = 0u,
                Reserved = new uint[1]
            };

            if (State == DriverConnectionState.KernelModeConnected && _deviceHandle != null && !_deviceHandle.IsInvalid)
            {
                int size = Marshal.SizeOf<OSUKERNEL_SETTINGS>();
                IntPtr inBuffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(settings, inBuffer, false);
                    lock (_ioLock)
                    {
                        return DeviceIoControl(
                            _deviceHandle,
                            IOCTL_OSUKERNEL_SET_CONFIG,
                            inBuffer,
                            (uint)size,
                            IntPtr.Zero,
                            0,
                            out _,
                            IntPtr.Zero
                        );
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }

            return true;
        }

        public bool QueryStatsRaw(ref OSUKERNEL_DRIVER_STATS stats)
        {
            if (State == DriverConnectionState.KernelModeConnected && _deviceHandle != null && !_deviceHandle.IsInvalid)
            {
                lock (_ioLock)
                {
                    bool success = DeviceIoControl(
                        _deviceHandle,
                        IOCTL_OSUKERNEL_GET_STATS,
                        IntPtr.Zero,
                        0,
                        _statsBuffer,
                        (uint)_statsBufferSize,
                        out uint bytesReturned,
                        IntPtr.Zero
                    );

                    if (success && bytesReturned >= _statsBufferSize)
                    {
                        unsafe
                        {
                            stats = *(OSUKERNEL_DRIVER_STATS*)_statsBuffer.ToPointer();
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        public OSUKERNEL_DRIVER_STATS QueryStats(TabletAreaModel model)
        {
            if (QueryStatsRaw(ref _currentStats))
            {
                return _currentStats;
            }

            // In user-mode fallback, calculate coordinates from current mouse cursor for live feedback
            GetCursorPos(out POINT pt);
            _simulatedStats.LastScreenX = pt.X;
            _simulatedStats.LastScreenY = pt.Y;
            _simulatedStats.PacketsPerSecond = 0; // No real packets in user-mode fallback
            _simulatedStats.TotalPackets++;
            _simulatedStats.InProximity = 1;

            // Map screen coords back to approximate tablet position for visual preview
            if (model.ScreenWidth > 0 && model.ScreenHeight > 0)
            {
                double normX = Math.Clamp((double)(pt.X - model.ScreenX) / model.ScreenWidth, 0.0, 1.0);
                double normY = Math.Clamp((double)(pt.Y - model.ScreenY) / model.ScreenHeight, 0.0, 1.0);
                _simulatedStats.LastRawX = (ushort)(model.XCounts + (normX * model.WidthCounts));
                _simulatedStats.LastRawY = (ushort)(model.YCounts + (normY * model.HeightCounts));
            }

            return _simulatedStats;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                lock (_ioLock)
                {
                    _deviceHandle?.Dispose();
                    _deviceHandle = null;
                }
                if (_statsBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_statsBuffer);
                }
                _isDisposed = true;
            }
            GC.SuppressFinalize(this);
        }

        // Win32 Native Methods
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            [MarshalAs(UnmanagedType.U4)] FileAccess dwDesiredAccess,
            [MarshalAs(UnmanagedType.U4)] FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            [MarshalAs(UnmanagedType.U4)] FileMode dwCreationDisposition,
            [MarshalAs(UnmanagedType.U4)] FileAttributes dwFlagsAndAttributes,
            IntPtr hTemplateFile
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped
        );

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
