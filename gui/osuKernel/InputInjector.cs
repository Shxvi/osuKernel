using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace osuKernel
{
    /// <summary>
    /// Ultra-low latency cursor placement and input injection service for osu!Kernel.
    /// Operates on a dedicated real-time thread with sub-microsecond scheduling, zero GC allocations
    /// in the hot path, and a continuous critically damped predictive interpolation engine up to 8 kHz.
    /// </summary>
    public class InputInjector : IDisposable
    {
        private readonly DriverInterop _driverInterop;
        private Thread? _workerThread;
        private volatile bool _isRunning;

        private bool _prevTipDown;
        private bool _prevBtn1Down;
        private bool _prevBtn2Down;
        private ulong _lastTotalPackets;
        private bool _wasInProximity;
        private OSUKERNEL_DRIVER_STATS _latestStats;

        // Cached virtual screen metrics for SendInput absolute coordinate mapping
        private int _virtScreenX;
        private int _virtScreenY;
        private int _virtScreenW;
        private int _virtScreenH;
        private int _metricsRefreshCounter;

        // Continuous C^1 Trajectory State for Zero-Gap Overclock (1kHz - 8kHz)
        private double _currPosX;
        private double _currPosY;
        private double _currVelX;
        private double _currVelY;
        private double _targetPosX;
        private double _targetPosY;
        private double _targetVelX;
        private double _targetVelY;
        private double _rawLastX;
        private double _rawLastY;
        private long _lastPacketArrivalTicks;
        private double _omegaN = 280.0;
        private bool _hasContinuousState;

        // Real-time dispatch PPS metrics
        private uint _dispatchedPackets;
        private uint _effectiveDispatchRate;
        private long _lastRateReportTicks;

        // Pre-allocated static input buffers to eliminate all managed GC allocations in hot loop
        private readonly INPUT[] _moveInputs = new INPUT[1];
        private readonly INPUT[] _buttonInputs = new INPUT[1];
        private static readonly int InputSize = Marshal.SizeOf<INPUT>();

        public TabletAreaModel? Model { get; set; }
        public uint DispatchRate => _effectiveDispatchRate;

        public InputInjector(DriverInterop driverInterop, TabletAreaModel? model = null)
        {
            _driverInterop = driverInterop;
            Model = model;

            // Pre-initialize move input structure
            _moveInputs[0].type = INPUT_MOUSE;
            _moveInputs[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;

            // Pre-initialize button input structure
            _buttonInputs[0].type = INPUT_MOUSE;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _workerThread = new Thread(InputLoop)
            {
                Name = "osuKernel_InputDispatcher_8kHz",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _workerThread.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(500);
            }
            ReleaseAllButtons();
        }

        private void InputLoop()
        {
            EnsureInputDesktopAttached();
            TimeBeginPeriod(1);

            long ticksPerSec = Stopwatch.Frequency;
            _lastRateReportTicks = Stopwatch.GetTimestamp();
            long nextDeadlineTicks = _lastRateReportTicks;

            try
            {
                RefreshVirtualScreenMetrics();
                while (_isRunning)
                {
                    // Refresh virtual screen metrics ~once per second to handle monitor changes
                    if (++_metricsRefreshCounter >= 4000)
                    {
                        _metricsRefreshCounter = 0;
                        RefreshVirtualScreenMetrics();
                    }

                    if (_driverInterop.State == DriverConnectionState.KernelModeConnected)
                    {
                        if (_driverInterop.QueryStatsRaw(ref _latestStats))
                        {
                            if (_latestStats.InProximity != 0)
                            {
                                int rate = (Model != null) ? Model.InterpolationRate : 0;
                                long nowTicks = Stopwatch.GetTimestamp();

                                // 1. Handle incoming hardware reports from tablet
                                if (_latestStats.TotalPackets != _lastTotalPackets)
                                {
                                    long dtTicks = nowTicks - _lastPacketArrivalTicks;
                                    double dtSec = (double)dtTicks / ticksPerSec;

                                    if (!_hasContinuousState || dtSec > 0.05) // First packet or pen re-entered proximity
                                    {
                                        _rawLastX = _latestStats.LastScreenX;
                                        _rawLastY = _latestStats.LastScreenY;
                                        _currPosX = _rawLastX;
                                        _currPosY = _rawLastY;
                                        _targetPosX = _rawLastX;
                                        _targetPosY = _rawLastY;
                                        _currVelX = 0;
                                        _currVelY = 0;
                                        _targetVelX = 0;
                                        _targetVelY = 0;
                                        _hasContinuousState = true;
                                        _omegaN = 280.0;
                                    }
                                    else if (dtSec > 0.0001)
                                    {
                                        // Compute instantaneous hardware velocity in screen coordinates
                                        double hwVx = (_latestStats.LastScreenX - _rawLastX) / dtSec;
                                        double hwVy = (_latestStats.LastScreenY - _rawLastY) / dtSec;

                                        _rawLastX = _latestStats.LastScreenX;
                                        _rawLastY = _latestStats.LastScreenY;

                                        // Natural tracking frequency adapted to hardware packet interval
                                        double targetOmega = Math.Clamp(3.2 / dtSec, 150.0, 1000.0);
                                        _omegaN = _omegaN * 0.35 + targetOmega * 0.65;

                                        bool enablePrediction = Model?.EnablePrediction == true;
                                        if (enablePrediction)
                                        {
                                            double lookaheadSec = Math.Clamp((Model?.PredictionLookaheadMs ?? 1.5) / 1000.0, 0.0, 0.015);
                                            _targetPosX = _rawLastX + (hwVx * lookaheadSec);
                                            _targetPosY = _rawLastY + (hwVy * lookaheadSec);
                                            _targetVelX = hwVx;
                                            _targetVelY = hwVy;
                                        }
                                        else
                                        {
                                            // Pure monotonic, zero-overshoot trajectory interpolation without lead projection
                                            _targetPosX = _rawLastX;
                                            _targetPosY = _rawLastY;
                                            _targetVelX = 0;
                                            _targetVelY = 0;
                                        }
                                    }

                                    _lastTotalPackets = _latestStats.TotalPackets;
                                    _lastPacketArrivalTicks = nowTicks;

                                    // Process physical tip contact & buttons (Pure digital, zero pressure overhead)
                                    ProcessButtons(_latestStats.LastButtons);

                                    // If native rate selected, dispatch directly on hardware arrival
                                    if (rate <= 0)
                                    {
                                        if (Model?.EnablePrediction == true)
                                        {
                                            UpdateCursorPosition(_targetPosX, _targetPosY);
                                        }
                                        else
                                        {
                                            UpdateCursorPosition(_latestStats.LastScreenX, _latestStats.LastScreenY);
                                        }
                                    }
                                }

                                // 2. Sub-packet trajectory dispatch (1kHz - 8kHz)
                                long stepTicks = (rate > 0) ? (ticksPerSec / rate) : (ticksPerSec / 4000);

                                if (rate > 0 && _hasContinuousState)
                                {
                                    double subDtSec = (double)stepTicks / ticksPerSec;

                                    // Integrate 2nd-order critically damped tracking filter
                                    double errX = _targetPosX - _currPosX;
                                    double errY = _targetPosY - _currPosY;
                                    double accX = (2.0 * _omegaN * (_targetVelX - _currVelX)) + ((_omegaN * _omegaN) * errX);
                                    double accY = (2.0 * _omegaN * (_targetVelY - _currVelY)) + ((_omegaN * _omegaN) * errY);

                                    _currVelX += accX * subDtSec;
                                    _currVelY += accY * subDtSec;
                                    _currPosX += _currVelX * subDtSec;
                                    _currPosY += _currVelY * subDtSec;

                                    // Stationary lock: prevent micro-drift when pen is hovering stationary
                                    if (Math.Abs(_targetVelX) < 1.0 && Math.Abs(_targetVelY) < 1.0 &&
                                        Math.Abs(errX) < 0.25 && Math.Abs(errY) < 0.25)
                                    {
                                        _currPosX = _rawLastX;
                                        _currPosY = _rawLastY;
                                        _currVelX = 0;
                                        _currVelY = 0;
                                    }

                                    UpdateCursorPosition(_currPosX, _currPosY);
                                }

                                _wasInProximity = true;

                                // Update live dispatch PPS rate metrics (4 times per second)
                                if (nowTicks - _lastRateReportTicks >= (ticksPerSec / 4))
                                {
                                    double elapsedSec = (nowTicks - _lastRateReportTicks) / (double)ticksPerSec;
                                    if (elapsedSec > 0)
                                    {
                                        _effectiveDispatchRate = (uint)Math.Round(_dispatchedPackets / elapsedSec);
                                        _dispatchedPackets = 0;
                                    }
                                    _lastRateReportTicks = nowTicks;
                                }

                                // Absolute deadline scheduling with sub-microsecond precision
                                nextDeadlineTicks += stepTicks;
                                long curTicks = Stopwatch.GetTimestamp();

                                if (curTicks < nextDeadlineTicks)
                                {
                                    while (Stopwatch.GetTimestamp() < nextDeadlineTicks)
                                    {
                                        Thread.SpinWait(4);
                                    }
                                }
                                else if (curTicks - nextDeadlineTicks > stepTicks * 2)
                                {
                                    nextDeadlineTicks = curTicks + stepTicks;
                                }
                                continue;
                            }
                            else
                            {
                                if (_wasInProximity)
                                {
                                    ReleaseAllButtons();
                                    _wasInProximity = false;
                                    _hasContinuousState = false;
                                    _effectiveDispatchRate = 0;
                                    _dispatchedPackets = 0;
                                }
                                Thread.Sleep(4);
                                nextDeadlineTicks = Stopwatch.GetTimestamp();
                                continue;
                            }
                        }
                    }

                    Thread.Sleep(50);
                    nextDeadlineTicks = Stopwatch.GetTimestamp();
                }
            }
            finally
            {
                ReleaseAllButtons();
                TimeEndPeriod(1);
            }
        }

        private static void EnsureInputDesktopAttached()
        {
            try
            {
                IntPtr hDesk = OpenInputDesktop(0, false, 0x01FF);
                if (hDesk != IntPtr.Zero)
                {
                    SetThreadDesktop(hDesk);
                    CloseDesktop(hDesk);
                }
            }
            catch { }
        }

        private void RefreshVirtualScreenMetrics()
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w > 0 && h > 0)
            {
                _virtScreenX = x;
                _virtScreenY = y;
                _virtScreenW = w;
                _virtScreenH = h;
            }
            else if (_virtScreenW == 0)
            {
                _virtScreenX = 0;
                _virtScreenY = 0;
                _virtScreenW = 1920;
                _virtScreenH = 1080;
            }
        }

        private void UpdateCursorPosition(double screenX, double screenY)
        {
            int normX = (int)Math.Round(((screenX - _virtScreenX) * 65535.0) / (_virtScreenW > 0 ? _virtScreenW : 1));
            int normY = (int)Math.Round(((screenY - _virtScreenY) * 65535.0) / (_virtScreenH > 0 ? _virtScreenH : 1));

            _moveInputs[0].mi.dx = Math.Clamp(normX, 0, 65535);
            _moveInputs[0].mi.dy = Math.Clamp(normY, 0, 65535);

            if (SendInput(1, _moveInputs, InputSize) == 0)
            {
                EnsureInputDesktopAttached();
                if (SendInput(1, _moveInputs, InputSize) == 0)
                {
                    SetCursorPos((int)Math.Round(screenX), (int)Math.Round(screenY));
                }
            }
            _dispatchedPackets++;
        }

        private void ProcessButtons(byte buttons)
        {
            // Tip click is active if enabled in Model AND hardware bit 0 is set
            bool tipClickEnabled = Model?.EnableTipClick ?? false;
            bool tipDown = tipClickEnabled && ((buttons & 0x01) != 0);
            bool btn1Down = (buttons & 0x02) != 0;
            bool btn2Down = (buttons & 0x04) != 0;

            if (tipDown != _prevTipDown)
            {
                SendMouseButton(tipDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP);
                _prevTipDown = tipDown;
            }

            if (btn1Down != _prevBtn1Down)
            {
                SendMouseButton(btn1Down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP);
                _prevBtn1Down = btn1Down;
            }

            if (btn2Down != _prevBtn2Down)
            {
                SendMouseButton(btn2Down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP);
                _prevBtn2Down = btn2Down;
            }
        }

        private void ReleaseAllButtons()
        {
            if (_prevTipDown)
            {
                SendMouseButton(MOUSEEVENTF_LEFTUP);
                _prevTipDown = false;
            }
            if (_prevBtn1Down)
            {
                SendMouseButton(MOUSEEVENTF_RIGHTUP);
                _prevBtn1Down = false;
            }
            if (_prevBtn2Down)
            {
                SendMouseButton(MOUSEEVENTF_MIDDLEUP);
                _prevBtn2Down = false;
            }
        }

        private void SendMouseButton(uint mouseFlag)
        {
            _buttonInputs[0].mi.dwFlags = mouseFlag;
            if (SendInput(1, _buttonInputs, InputSize) == 0)
            {
                EnsureInputDesktopAttached();
                SendInput(1, _buttonInputs, InputSize);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        // ================= Win32 Interop =================

        private const int INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);
    }
}
