using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using System.Windows.Interop;

namespace osuKernel
{
    public partial class MainWindow : Window
    {
        private double PxPerMm => 760.0 / Math.Max(1.0, _model.PhysicalWidthMm);

        private readonly TabletAreaModel _model = new();
        private readonly DriverInterop _driverInterop = new();
        private readonly InputInjector _inputInjector;
        private readonly DispatcherTimer _telemetryTimer = new();
        private readonly System.Diagnostics.Stopwatch _ppsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        private ulong _prevTotalPackets = 0;
        private uint _calculatedPps = 0;
        private int _reconnectCheckCounter = 0;

        private bool _isUpdatingUi = false;

        // Interactive Drag State
        private bool _isDraggingArea = false;
        private bool _isResizingArea = false;
        private string _resizeHandleTag = string.Empty;
        private Point _dragStartPoint;
        private double _initialXMm;
        private double _initialYMm;
        private double _initialWidthMm;
        private double _initialHeightMm;

        private AppTrayIcon? _notifyIcon;
        private bool _isExplicitExit = false;
        private bool _isInitialized = false;

        public MainWindow()
        {
            _isUpdatingUi = true;
            InitializeComponent();
            _inputInjector = new InputInjector(_driverInterop, _model);

            InitializeDriverService();

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            Closing += MainWindow_Closing;
        }

        private void InitializeDriverService()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            _isUpdatingUi = true;
            try
            {
                // Initialize System Tray Icon for zero-GUI background operation
                InitializeTrayIcon();

                // Connect to Driver or activate Fallback
                _driverInterop.Connect();
                UpdateDriverStatusUi();

                // Populate Monitors
                RefreshMonitorsList();

                // Load designated startup profile (autoload preset or last active session)
                var activeProfile = ProfileManager.LoadStartupProfile();
                _model.ApplyProfile(activeProfile);

                // Match monitor dropdown to profile's screen
                SelectMatchingMonitor(activeProfile.ScreenX, activeProfile.ScreenY);

                // Populate Profiles dropdowns
                RefreshProfilesLists();

                // Start dedicated high-precision input injector directly in interactive session
                _inputInjector.Start();

                // Setup Telemetry Timer (~60 Hz)
                _telemetryTimer.Interval = TimeSpan.FromMilliseconds(16);
                _telemetryTimer.Tick += TelemetryTimer_Tick;
                _telemetryTimer.Start();

                // Check Windows startup registry state
                UpdateAutoStartCheckbox();

                // Initialize tablet selector
                InitTabletSelector();

                // Update UI state from model
                UpdateUiFromModel();
            }
            finally
            {
                _isUpdatingUi = false;
            }

            // Sync area configuration with driver
            SendAndSaveConfig();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProcThemeHook);
            }

            UpdateCanvasGeometry();
            UpdateUiFromModel();
        }

        private IntPtr WndProcThemeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == MaterialColorPalette.WM_DWMCOLORIZATIONCOLORCHANGED ||
                (msg == MaterialColorPalette.WM_SETTINGCHANGE && (wParam != IntPtr.Zero || lParam != IntPtr.Zero)))
            {
                MaterialColorPalette.ApplyDynamicPalette();
            }
            return IntPtr.Zero;
        }

        public void ResetAreaToFull()
        {
            _model.SetFullArea();
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        public void ExitApplication()
        {
            _isExplicitExit = true;
            Close();
        }

        private void InitializeTrayIcon()
        {
            if (_notifyIcon != null) return;

            _notifyIcon = new AppTrayIcon(this, "osu!Kernel");
            _notifyIcon.DoubleClick += () =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (!_isExplicitExit && ChkMinimizeToTray?.IsChecked == true)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _telemetryTimer.Stop();
            _inputInjector.Stop();
            _inputInjector.Dispose();
            _driverInterop.Dispose();
            if (_notifyIcon != null)
            {
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }

        // ================= Autostart Registry Configuration =================

        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "osuKernel";
        private const string LegacyRunValueName = "CTL472";

        private void UpdateAutoStartCheckbox()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                bool exists = (key?.GetValue(RunValueName) != null) || (key?.GetValue(LegacyRunValueName) != null);
                if (ChkStartWithWindows != null)
                {
                    ChkStartWithWindows.IsChecked = exists;
                }
            }
            catch { }
        }

        private void ChkStartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key == null) return;

                if (ChkStartWithWindows.IsChecked == true)
                {
                    string exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "osuKernel.exe");
                    key.SetValue(RunValueName, $"\"{exePath}\" --minimized");
                    key.DeleteValue(LegacyRunValueName, false);
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                    key.DeleteValue(LegacyRunValueName, false);
                }
            }
            catch { }
        }

        private void BtnExitApp_Click(object sender, RoutedEventArgs e)
        {
            _isExplicitExit = true;
            Close();
        }

        // ================= Driver Communication & Settings Persistence =================

        public void SendAndSaveConfig()
        {
            _driverInterop.SendAreaConfig(_model);
            _driverInterop.SendSettings(_model);
            ProfileManager.SaveActiveSettings(_model.ToProfile(_model.ActivePresetName));
            UpdatePresetModifiedStatus();
        }

        private void UpdateDriverStatusUi()
        {
            switch (_driverInterop.State)
            {
                case DriverConnectionState.KernelModeConnected:
                    DriverStatusDot.Fill = (Brush)FindResource("SuccessGreenBrush");
                    DriverStatusText.Text = "Kernel Driver Active";
                    break;
                case DriverConnectionState.UserModeFallbackActive:
                    DriverStatusDot.Fill = (Brush)FindResource("WarningAmberBrush");
                    DriverStatusText.Text = "User-Mode Fallback";
                    break;
                default:
                    DriverStatusDot.Fill = (Brush)FindResource("DangerRedBrush");
                    DriverStatusText.Text = "Device Disconnected";
                    break;
            }
        }

        // ================= Profile & Autoload Management =================

        private void RefreshProfilesLists()
        {
            var profiles = ProfileManager.GetAvailableProfiles();

            var quickList = new System.Collections.Generic.List<string>();
            if (!Array.Exists(profiles, p => string.Equals(p, _model.ActivePresetName, StringComparison.OrdinalIgnoreCase)))
            {
                quickList.Add(_model.ActivePresetName);
            }
            quickList.AddRange(profiles);

            // Populate Quick Presets Dropdown
            CmbQuickPresets.ItemsSource = null;
            CmbQuickPresets.ItemsSource = quickList;
            CmbQuickPresets.SelectedItem = _model.ActivePresetName;

            // Populate Tab 3 Profiles Dropdown
            CmbProfiles.ItemsSource = null;
            CmbProfiles.ItemsSource = profiles;
            CmbProfiles.SelectedItem = _model.ActivePresetName;

            UpdateProfileDetailsCard();
        }


        private void UpdatePresetModifiedStatus()
        {
            if (TxtPresetModifiedBadge == null) return;

            if (_model.IsModified)
            {
                TxtPresetModifiedBadge.Text = "● Unsaved";
                TxtPresetModifiedBadge.Foreground = (Brush)FindResource("WarningAmberBrush");
            }
            else
            {
                TxtPresetModifiedBadge.Text = "● Saved";
                TxtPresetModifiedBadge.Foreground = (Brush)FindResource("SuccessGreenBrush");
            }
        }

        private void CmbQuickPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbQuickPresets.SelectedItem is string name)
            {
                LoadNamedProfile(name);
            }
        }

        private void BtnQuickSavePreset_Click(object sender, RoutedEventArgs e)
        {
            string name = _model.ActivePresetName;
            var profile = _model.ToProfile(name);
            ProfileManager.SaveProfile(profile);
            _model.IsModified = false;
            SendAndSaveConfig();
            RefreshProfilesLists();
        }

        private void BtnGoToProfiles_Click(object sender, RoutedEventArgs e)
        {
            if (TabBtnProfiles != null)
            {
                TabBtnProfiles.IsChecked = true;
            }
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateProfileDetailsCard();
        }

        private void UpdateProfileDetailsCard()
        {
            if (TxtProfileInfoDims == null) return;

            string? selectedName = CmbProfiles?.SelectedItem as string ?? _model.ActivePresetName;
            var profile = ProfileManager.LoadProfile(selectedName);
            if (profile != null)
            {
                TxtProfileInfoDims.Text = $"{profile.WidthMm:F1} × {profile.HeightMm:F1} mm";
                TxtProfileInfoOffset.Text = $"{profile.XMm:F1}, {profile.YMm:F1} mm";
                TxtProfileInfoRotation.Text = $"{profile.Rotation}°";

                bool isAuto = string.Equals(ProfileManager.GetAutoloadPreset(), profile.Name, StringComparison.OrdinalIgnoreCase);
                TxtProfileInfoAutoload.Text = isAuto ? "★ Yes (Default)" : "No";
                TxtProfileInfoAutoload.Foreground = isAuto
                    ? (Brush)FindResource("StarGoldBrush")
                    : (Brush)FindResource("TextSecondaryBrush");
            }
        }

        private void BtnLoadSelectedProfile_Click(object sender, RoutedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is string name)
            {
                LoadNamedProfile(name);
            }
        }

        private void LoadNamedProfile(string name)
        {
            var profile = ProfileManager.LoadProfile(name);
            if (profile != null)
            {
                _isUpdatingUi = true;
                try
                {
                    _model.ApplyProfile(profile);
                    _model.ActivePresetName = name;
                    _model.IsModified = false;

                    CmbQuickPresets.SelectedItem = name;
                    CmbProfiles.SelectedItem = name;

                    UpdateUiFromModel();
                }
                finally
                {
                    _isUpdatingUi = false;
                }

                SendAndSaveConfig();
            }
        }

        private void BtnOverwriteProfile_Click(object sender, RoutedEventArgs e)
        {
            string? name = CmbProfiles.SelectedItem as string ?? _model.ActivePresetName;
            var profile = _model.ToProfile(name);
            ProfileManager.SaveProfile(profile);
            _model.ActivePresetName = name;
            _model.IsModified = false;
            SendAndSaveConfig();
            RefreshProfilesLists();
        }

        private void BtnSetAutoloadProfile_Click(object sender, RoutedEventArgs e)
        {
            string? name = CmbProfiles.SelectedItem as string ?? _model.ActivePresetName;
            ProfileManager.SetAutoloadPreset(name);
            SendAndSaveConfig();
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is string name)
            {
                if (ProfileManager.DeleteProfile(name))
                {
                    RefreshProfilesLists();
                }
            }
        }

        private void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
        {
            string name = string.IsNullOrWhiteSpace(TxtNewProfileName.Text)
                ? $"Area {DateTime.Now:MM-dd HHmm}"
                : TxtNewProfileName.Text.Trim();

            var profile = _model.ToProfile(name);
            ProfileManager.SaveProfile(profile);
            _model.ActivePresetName = name;
            _model.IsModified = false;

            RefreshProfilesLists();
            CmbProfiles.SelectedItem = name;
            CmbQuickPresets.SelectedItem = name;
            SendAndSaveConfig();
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            ProfileManager.OpenProfilesFolder();
        }

        private void RadioAutoload_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;

            if (RadioAutoloadLastSession?.IsChecked == true)
            {
                ProfileManager.SetAutoloadPreset("LastSession");
            }
            else if (RadioAutoloadPreset?.IsChecked == true)
            {
                ProfileManager.SetAutoloadPreset(_model.ActivePresetName);
            }

            SendAndSaveConfig();
        }

        // ================= Tablet Hardware Selection =================

        private void InitTabletSelector()
        {
            if (CmbTabletModel == null) return;
            CmbTabletModel.Items.Clear();

            var autoItem = new ComboBoxItem
            {
                Content = "Auto-Detect (Connected Tablet)",
                Tag = "Auto"
            };
            CmbTabletModel.Items.Add(autoItem);

            foreach (var tablet in TabletDatabase.AllTablets)
            {
                var item = new ComboBoxItem
                {
                    Content = tablet.DisplayName,
                    Tag = tablet.ModelId
                };
                CmbTabletModel.Items.Add(item);
            }

            SyncTabletSelection();
        }

        private void SyncTabletSelection()
        {
            if (CmbTabletModel == null) return;

            string targetTag = _model.SelectedTabletModel ?? "Auto";
            foreach (ComboBoxItem item in CmbTabletModel.Items)
            {
                if (item.Tag is string tag && string.Equals(tag, targetTag, StringComparison.OrdinalIgnoreCase))
                {
                    CmbTabletModel.SelectedItem = item;
                    break;
                }
            }

            var detected = TabletDatabase.DetectConnectedTablet();
            if (TxtDetectedTabletHint != null)
            {
                TxtDetectedTabletHint.Text = $"Detected: {detected.DisplayName}";
            }

            if (TxtTabletDeviceDesc != null)
            {
                TxtTabletDeviceDesc.Text = _model.ActiveTablet.DisplayName;
            }
        }

        private void CmbTabletModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi || CmbTabletModel.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
                return;

            _model.SelectedTabletModel = tag;
            if (string.Equals(tag, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                _model.ActiveTablet = TabletDatabase.DetectConnectedTablet();
            }
            else
            {
                _model.ActiveTablet = TabletDatabase.GetByModelId(tag);
            }

            if (TxtTabletDeviceDesc != null)
            {
                TxtTabletDeviceDesc.Text = _model.ActiveTablet.DisplayName;
            }

            UpdateCanvasGeometry();
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        // ================= Tab Navigation =================

        private void TabBtn_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewAreaMapping == null || ViewDisplayOutput == null || ViewProfilesSettings == null)
            {
                return;
            }

            ViewAreaMapping.Visibility = (TabBtnArea.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            ViewDisplayOutput.Visibility = (TabBtnDisplay.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            ViewProfilesSettings.Visibility = (TabBtnProfiles.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= Canvas Geometry & Layout =================

        private void UpdateCanvasGeometry()
        {
            if (TabletCanvas == null || ActiveAreaBorder == null) return;

            double targetHeight = _model.PhysicalHeightMm * PxPerMm;
            TabletCanvas.Height = Math.Round(targetHeight);

            double areaLeft = _model.LeftMm * PxPerMm;
            double areaTop = _model.TopMm * PxPerMm;
            double areaWidth = _model.WidthMm * PxPerMm;
            double areaHeight = _model.HeightMm * PxPerMm;

            Canvas.SetLeft(ActiveAreaBorder, areaLeft);
            Canvas.SetTop(ActiveAreaBorder, areaTop);
            ActiveAreaBorder.Width = Math.Max(20, areaWidth);
            ActiveAreaBorder.Height = Math.Max(20, areaHeight);

            ActiveAreaBorder.RenderTransformOrigin = new Point(0.5, 0.5);
            ActiveAreaBorder.RenderTransform = new RotateTransform(_model.Rotation);

            if (AreaHandlesContainer != null)
            {
                Canvas.SetLeft(AreaHandlesContainer, areaLeft);
                Canvas.SetTop(AreaHandlesContainer, areaTop);
                AreaHandlesContainer.Width = Math.Max(20, areaWidth);
                AreaHandlesContainer.Height = Math.Max(20, areaHeight);
                AreaHandlesContainer.RenderTransformOrigin = new Point(0.5, 0.5);
                AreaHandlesContainer.RenderTransform = new RotateTransform(_model.Rotation);
                AreaHandlesContainer.Visibility = _model.ShowAreaHandles ? Visibility.Visible : Visibility.Collapsed;
            }

            AreaDimsText.Text = $"{_model.WidthMm:F1} × {_model.HeightMm:F1} mm";
            AreaOffsetsText.Text = $"X: {_model.XMm:F1} mm | Y: {_model.YMm:F1} mm";
        }

        // ================= Interactive Mouse Drag & 8-Handle Resizing =================

        private void ActiveArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isResizingArea)
            {
                _isDraggingArea = true;
                _dragStartPoint = e.GetPosition(TabletCanvas);
                _initialXMm = _model.XMm;
                _initialYMm = _model.YMm;
                ActiveAreaBorder.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && sender is Ellipse handle && handle.Tag is string tag)
            {
                _isResizingArea = true;
                _resizeHandleTag = tag;
                _dragStartPoint = e.GetPosition(TabletCanvas);
                _initialXMm = _model.XMm;
                _initialYMm = _model.YMm;
                _initialWidthMm = _model.WidthMm;
                _initialHeightMm = _model.HeightMm;
                handle.CaptureMouse();
                e.Handled = true;
            }
        }

        private void TabletCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point currentPoint = e.GetPosition(TabletCanvas);
            double deltaX = (currentPoint.X - _dragStartPoint.X) / PxPerMm;
            double deltaY = (currentPoint.Y - _dragStartPoint.Y) / PxPerMm;

            if (_isDraggingArea)
            {
                _model.XMm = _initialXMm + deltaX;
                _model.YMm = _initialYMm + deltaY;
                _model.IsModified = true;
                UpdateUiFromModel();
                _driverInterop.SendAreaConfig(_model);
            }
            else if (_isResizingArea)
            {
                double targetRatio = _model.TargetAspectRatio;
                double initLeft = _initialXMm - (_initialWidthMm / 2.0);
                double initRight = _initialXMm + (_initialWidthMm / 2.0);
                double initTop = _initialYMm - (_initialHeightMm / 2.0);
                double initBottom = _initialYMm + (_initialHeightMm / 2.0);

                switch (_resizeHandleTag)
                {
                    case "BottomRight":
                        double newW = Math.Max(5.0, _initialWidthMm + deltaX);
                        double newH = _model.LockAspectRatio ? (newW / targetRatio) : Math.Max(5.0, _initialHeightMm + deltaY);
                        _model.WidthMm = newW;
                        _model.HeightMm = newH;
                        _model.XMm = initLeft + (newW / 2.0);
                        _model.YMm = initTop + (newH / 2.0);
                        break;

                    case "BottomLeft":
                        double nwBL = Math.Max(5.0, _initialWidthMm - deltaX);
                        double nhBL = _model.LockAspectRatio ? (nwBL / targetRatio) : Math.Max(5.0, _initialHeightMm + deltaY);
                        _model.WidthMm = nwBL;
                        _model.HeightMm = nhBL;
                        _model.XMm = initRight - (nwBL / 2.0);
                        _model.YMm = initTop + (nhBL / 2.0);
                        break;

                    case "TopRight":
                        double nwTR = Math.Max(5.0, _initialWidthMm + deltaX);
                        double nhTR = _model.LockAspectRatio ? (nwTR / targetRatio) : Math.Max(5.0, _initialHeightMm - deltaY);
                        _model.WidthMm = nwTR;
                        _model.HeightMm = nhTR;
                        _model.XMm = initLeft + (nwTR / 2.0);
                        _model.YMm = initBottom - (nhTR / 2.0);
                        break;

                    case "TopLeft":
                        double nwTL = Math.Max(5.0, _initialWidthMm - deltaX);
                        double nhTL = _model.LockAspectRatio ? (nwTL / targetRatio) : Math.Max(5.0, _initialHeightMm - deltaY);
                        _model.WidthMm = nwTL;
                        _model.HeightMm = nhTL;
                        _model.XMm = initRight - (nwTL / 2.0);
                        _model.YMm = initBottom - (nhTL / 2.0);
                        break;

                    case "Top":
                        double nhT = Math.Max(5.0, _initialHeightMm - deltaY);
                        _model.HeightMm = nhT;
                        _model.YMm = initBottom - (nhT / 2.0);
                        break;

                    case "Bottom":
                        double nhB = Math.Max(5.0, _initialHeightMm + deltaY);
                        _model.HeightMm = nhB;
                        _model.YMm = initTop + (nhB / 2.0);
                        break;

                    case "Left":
                        double nwL = Math.Max(5.0, _initialWidthMm - deltaX);
                        _model.WidthMm = nwL;
                        _model.XMm = initRight - (nwL / 2.0);
                        break;

                    case "Right":
                        double nwR = Math.Max(5.0, _initialWidthMm + deltaX);
                        _model.WidthMm = nwR;
                        _model.XMm = initLeft + (nwR / 2.0);
                        break;
                }

                _model.IsModified = true;
                UpdateUiFromModel();
                _driverInterop.SendAreaConfig(_model);
            }
        }

        private void TabletCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingArea)
            {
                _isDraggingArea = false;
                ActiveAreaBorder.ReleaseMouseCapture();
                SendAndSaveConfig();
            }
            if (_isResizingArea)
            {
                _isResizingArea = false;
                _resizeHandleTag = string.Empty;
                Mouse.Capture(null);
                SendAndSaveConfig();
            }
        }

        private void TabletCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDraggingArea && !_isResizingArea)
            {
                Point pt = e.GetPosition(TabletCanvas);
                double clickedXMm = pt.X / PxPerMm;
                double clickedYMm = pt.Y / PxPerMm;

                _model.XMm = clickedXMm;
                _model.YMm = clickedYMm;
                _model.IsModified = true;
                UpdateUiFromModel();
                SendAndSaveConfig();
            }
        }

        // ================= UI Updates & Text Input =================

        private void UpdateUiFromModel()
        {
            _isUpdatingUi = true;
            try
            {
                TxtWidthMm.Text = _model.WidthMm.ToString("0.##", CultureInfo.InvariantCulture);
                TxtHeightMm.Text = _model.HeightMm.ToString("0.##", CultureInfo.InvariantCulture);
                TxtXMm.Text = _model.XMm.ToString("0.##", CultureInfo.InvariantCulture);
                TxtYMm.Text = _model.YMm.ToString("0.##", CultureInfo.InvariantCulture);
                ChkLockAspect.IsChecked = _model.LockAspectRatio;
                ChkAreaClipping.IsChecked = _model.AreaClipping;
                ChkAreaLimiting.IsChecked = _model.AreaLimiting;

                // Aspect Ratio controls
                if (CmbAspectRatio != null)
                {
                    foreach (ComboBoxItem item in CmbAspectRatio.Items)
                    {
                        if (item.Tag is string tag && string.Equals(tag, _model.AspectRatioPreset, StringComparison.OrdinalIgnoreCase))
                        {
                            CmbAspectRatio.SelectedItem = item;
                            break;
                        }
                    }
                }

                if (GridCustomRatio != null)
                {
                    GridCustomRatio.Visibility = string.Equals(_model.AspectRatioPreset, "Custom", StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
                if (TxtCustomRatioW != null) TxtCustomRatioW.Text = _model.CustomRatioX.ToString("G", CultureInfo.InvariantCulture);
                if (TxtCustomRatioH != null) TxtCustomRatioH.Text = _model.CustomRatioY.ToString("G", CultureInfo.InvariantCulture);

                if (SliderRotation != null) SliderRotation.Value = _model.Rotation;
                if (TxtRotation != null && !TxtRotation.IsFocused) TxtRotation.Text = _model.Rotation.ToString();

                // Area handles and pen cursor toggle
                if (ChkShowAreaHandles != null) ChkShowAreaHandles.IsChecked = _model.ShowAreaHandles;
                if (AreaHandlesContainer != null)
                {
                    AreaHandlesContainer.Visibility = _model.ShowAreaHandles ? Visibility.Visible : Visibility.Collapsed;
                }
                if (ChkShowPenCursor != null) ChkShowPenCursor.IsChecked = _model.ShowPenCursor;
                if (PenCrosshair != null && !_model.ShowPenCursor)
                {
                    PenCrosshair.Visibility = Visibility.Collapsed;
                }

                // Pen Tip & Developer Mode controls
                if (ChkEnableTipClick != null) ChkEnableTipClick.IsChecked = _model.EnableTipClick;
                if (ChkDevMode != null) ChkDevMode.IsChecked = _model.DevMode;

                // Dev Mode manual controls visibility
                var devVis = _model.DevMode ? Visibility.Visible : Visibility.Collapsed;
                if (PanelDevInterpolation != null) PanelDevInterpolation.Visibility = devVis;
                if (TxtCustomInterpolation != null && !TxtCustomInterpolation.IsFocused)
                {
                    TxtCustomInterpolation.Text = _model.InterpolationRate.ToString();
                }

                if (PanelDevAntichatter != null) PanelDevAntichatter.Visibility = devVis;
                if (TxtAntichatterManual != null && !TxtAntichatterManual.IsFocused)
                {
                    TxtAntichatterManual.Text = _model.AntichatterDeadzone.ToString();
                }

                if (PanelDevSmoothing != null) PanelDevSmoothing.Visibility = devVis;
                if (TxtSmoothingManual != null && !TxtSmoothingManual.IsFocused)
                {
                    TxtSmoothingManual.Text = _model.SmoothingStrength.ToString();
                }

                if (PanelDevPrediction != null) PanelDevPrediction.Visibility = devVis;
                if (TxtPredictionManual != null && !TxtPredictionManual.IsFocused)
                {
                    TxtPredictionManual.Text = _model.PredictionLookaheadMs.ToString("0.##", CultureInfo.InvariantCulture);
                }

                // Overclock & Filter controls
                if (CmbOverclockRate != null)
                {
                    bool matchedPreset = false;
                    foreach (ComboBoxItem item in CmbOverclockRate.Items)
                    {
                        if (item == CmbItemCustomRate) continue;
                        if (item.Tag is string tagStr && int.TryParse(tagStr, out int tagVal) && tagVal == _model.InterpolationRate)
                        {
                            CmbOverclockRate.SelectedItem = item;
                            matchedPreset = true;
                            break;
                        }
                    }

                    if (!matchedPreset && CmbItemCustomRate != null)
                    {
                        CmbItemCustomRate.Content = $"Custom ({_model.InterpolationRate} Hz)";
                        CmbItemCustomRate.Visibility = Visibility.Visible;
                        CmbOverclockRate.SelectedItem = CmbItemCustomRate;
                    }
                    else if (CmbItemCustomRate != null)
                    {
                        CmbItemCustomRate.Visibility = Visibility.Collapsed;
                    }
                }

                UpdateActiveRateBadge();

                if (ChkAntichatter != null) ChkAntichatter.IsChecked = _model.AntichatterDeadzone > 0;
                if (SliderAntichatter != null)
                {
                    SliderAntichatter.Value = Math.Clamp(_model.AntichatterDeadzone, 0, 30);
                    SliderAntichatter.IsEnabled = (_model.AntichatterDeadzone > 0) || _model.DevMode;
                }
                if (TxtAntichatterVal != null)
                {
                    TxtAntichatterVal.Text = (_model.AntichatterDeadzone == 0)
                        ? "0 counts (Off)"
                        : $"{_model.AntichatterDeadzone} counts (~{_model.AntichatterDeadzone * 0.01:F2} mm)";
                }

                if (ChkSmoothing != null) ChkSmoothing.IsChecked = _model.EnableSmoothing;
                if (SliderSmoothing != null)
                {
                    SliderSmoothing.Value = Math.Clamp(_model.SmoothingStrength, 5, 90);
                    SliderSmoothing.IsEnabled = _model.EnableSmoothing || _model.DevMode;
                }
                if (TxtSmoothingVal != null) TxtSmoothingVal.Text = $"{_model.SmoothingStrength}%";

                if (ChkPrediction != null) ChkPrediction.IsChecked = _model.EnablePrediction;
                if (SliderPrediction != null)
                {
                    SliderPrediction.Value = Math.Clamp(_model.PredictionLookaheadMs, 0.5, 5.0);
                    SliderPrediction.IsEnabled = _model.EnablePrediction || _model.DevMode;
                }
                if (TxtPredictionVal != null)
                {
                    TxtPredictionVal.Text = _model.EnablePrediction ? $"{_model.PredictionLookaheadMs:0.#} ms" : "Off";
                }

                if (ChkKernelDispatch != null) ChkKernelDispatch.IsChecked = _model.LowLatencyKernelMode;

                // Autoload mode radio buttons
                string autoPreset = ProfileManager.GetAutoloadPreset();
                bool isLastSession = string.Equals(autoPreset, "LastSession", StringComparison.OrdinalIgnoreCase);
                if (RadioAutoloadLastSession != null) RadioAutoloadLastSession.IsChecked = isLastSession;
                if (RadioAutoloadPreset != null) RadioAutoloadPreset.IsChecked = !isLastSession;

                // Sync tablet model selection
                SyncTabletSelection();

                UpdateCanvasGeometry();
                UpdatePresetModifiedStatus();
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void AreaInput_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitAreaTextInput();
        }

        private void AreaInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitAreaTextInput();
                // Move focus away to commit
                Keyboard.ClearFocus();
            }
        }

        private static bool TryParseFloat(string? text, out double value)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                value = 0;
                return false;
            }
            string normalized = text.Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void CommitAreaTextInput()
        {
            if (_isUpdatingUi) return;

            if (TryParseFloat(TxtWidthMm.Text, out double w) &&
                TryParseFloat(TxtHeightMm.Text, out double h) &&
                TryParseFloat(TxtXMm.Text, out double x) &&
                TryParseFloat(TxtYMm.Text, out double y))
            {
                bool prevLock = _model.LockAspectRatio;
                try
                {
                    // Temporarily prevent cross-recalculation if user entered explicit w and h
                    if (Math.Abs(_model.WidthMm - w) > 0.001 && Math.Abs(_model.HeightMm - h) < 0.001 && prevLock)
                    {
                        // User changed Width, let aspect ratio adjust Height
                        _model.WidthMm = w;
                    }
                    else if (Math.Abs(_model.HeightMm - h) > 0.001 && Math.Abs(_model.WidthMm - w) < 0.001 && prevLock)
                    {
                        // User changed Height, let aspect ratio adjust Width
                        _model.HeightMm = h;
                    }
                    else
                    {
                        // Both changed or direct entry, assign without aspect ratio overriding
                        _model.LockAspectRatio = false;
                        _model.WidthMm = w;
                        _model.HeightMm = h;
                    }

                    _model.XMm = x;
                    _model.YMm = y;
                    _model.IsModified = true;
                }
                finally
                {
                    _model.LockAspectRatio = prevLock;
                }

                UpdateUiFromModel();
                SendAndSaveConfig();
            }
        }

        // ================= Preset Handlers =================

        private void BtnFullArea_Click(object sender, RoutedEventArgs e)
        {
            _model.SetFullArea();
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        private void BtnMatchDisplay_Click(object sender, RoutedEventArgs e)
        {
            _model.MatchDisplayRatio();
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        private void BtnCenterArea_Click(object sender, RoutedEventArgs e)
        {
            _model.CenterArea();
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        // ================= Aspect Ratio Handlers =================

        private void CmbAspectRatio_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbAspectRatio.SelectedItem is ComboBoxItem item && item.Tag is string preset)
            {
                _model.AspectRatioPreset = preset;
                if (GridCustomRatio != null)
                {
                    GridCustomRatio.Visibility = string.Equals(preset, "Custom", StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
                if (_model.LockAspectRatio)
                {
                    _model.UpdateHeightForAspectRatio();
                    _model.IsModified = true;
                    UpdateUiFromModel();
                    SendAndSaveConfig();
                }
                else
                {
                    _model.IsModified = true;
                    UpdatePresetModifiedStatus();
                    ProfileManager.SaveActiveSettings(_model.ToProfile(_model.ActivePresetName));
                }
            }
        }

        private void BtnApplyCustomRatio_Click(object sender, RoutedEventArgs e)
        {
            CommitCustomRatio();
        }

        private void CustomRatio_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitCustomRatio();
        }

        private void CustomRatio_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitCustomRatio();
                Keyboard.ClearFocus();
            }
        }

        private void CommitCustomRatio()
        {
            if (_isUpdatingUi) return;
            if (TryParseFloat(TxtCustomRatioW.Text, out double rw) &&
                TryParseFloat(TxtCustomRatioH.Text, out double rh) &&
                rw > 0 && rh > 0)
            {
                _model.CustomRatioX = rw;
                _model.CustomRatioY = rh;
                _model.AspectRatioPreset = "Custom";
                if (_model.LockAspectRatio)
                {
                    _model.UpdateHeightForAspectRatio();
                    _model.IsModified = true;
                    UpdateUiFromModel();
                    SendAndSaveConfig();
                }
                else
                {
                    _model.IsModified = true;
                    UpdatePresetModifiedStatus();
                    ProfileManager.SaveActiveSettings(_model.ToProfile(_model.ActivePresetName));
                }
            }
        }

        // ================= Actions & Orientation (-180 to +180 degrees) =================
 
        private void SliderRotation_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi) return;
            int angle = Math.Clamp((int)Math.Round(e.NewValue), -180, 180);
            _model.Rotation = angle;
            _model.IsModified = true;
            if (TxtRotation != null && !TxtRotation.IsFocused)
            {
                TxtRotation.Text = angle.ToString();
            }
            UpdateCanvasGeometry();
            SendAndSaveConfig();
        }

        private void TxtRotation_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitRotationText();
        }

        private void TxtRotation_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitRotationText();
                Keyboard.ClearFocus();
            }
        }

        private void CommitRotationText()
        {
            if (TxtRotation == null) return;
            if (int.TryParse(TxtRotation.Text.Trim().TrimEnd('°'), out int val))
            {
                SetRotationAngle(val);
            }
            else
            {
                TxtRotation.Text = _model.Rotation.ToString();
            }
        }

        private void BtnAngleNeg180_Click(object sender, RoutedEventArgs e) => SetRotationAngle(-180);
        private void BtnAngleNeg90_Click(object sender, RoutedEventArgs e) => SetRotationAngle(-90);
        private void BtnAngle0_Click(object sender, RoutedEventArgs e) => SetRotationAngle(0);
        private void BtnAngle90_Click(object sender, RoutedEventArgs e) => SetRotationAngle(90);
        private void BtnAngle180_Click(object sender, RoutedEventArgs e) => SetRotationAngle(180);

        private void SetRotationAngle(int angle)
        {
            int targetAngle = _model.DevMode ? angle : Math.Clamp(angle, -180, 180);
            _model.Rotation = targetAngle;
            _model.IsModified = true;
            if (SliderRotation != null)
            {
                SliderRotation.Value = Math.Clamp(targetAngle, -180, 180);
            }
            if (TxtRotation != null && !TxtRotation.IsFocused)
            {
                TxtRotation.Text = targetAngle.ToString();
            }
            UpdateCanvasGeometry();
            SendAndSaveConfig();
        }

        private void ChkLockAspect_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.LockAspectRatio = ChkLockAspect.IsChecked == true;
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        private void ChkClipping_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.AreaClipping = ChkAreaClipping.IsChecked == true;
            _model.IsModified = true;
            SendAndSaveConfig();
        }

        private void ChkAreaLimiting_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.AreaLimiting = ChkAreaLimiting.IsChecked == true;
            _model.IsModified = true;
            SendAndSaveConfig();
        }

        private void ChkKernelDispatch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.LowLatencyKernelMode = ChkKernelDispatch.IsChecked == true;
            SendAndSaveConfig();
        }

        private void ChkShowAreaHandles_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.ShowAreaHandles = (ChkShowAreaHandles.IsChecked == true);
            if (AreaHandlesContainer != null)
            {
                AreaHandlesContainer.Visibility = _model.ShowAreaHandles ? Visibility.Visible : Visibility.Collapsed;
            }
            _model.IsModified = true;
            SendAndSaveConfig();
        }

        private void ChkShowPenCursor_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.ShowPenCursor = (ChkShowPenCursor.IsChecked == true);
            if (PenCrosshair != null && !_model.ShowPenCursor)
            {
                PenCrosshair.Visibility = Visibility.Collapsed;
            }
            _model.IsModified = true;
            SendAndSaveConfig();
        }

        // ================= Pen Tip & Developer Handlers =================

        private void ChkEnableTipClick_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.EnableTipClick = (ChkEnableTipClick.IsChecked == true);
            _model.IsModified = true;
            SendAndSaveConfig();
        }

        private void ChkDevMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.DevMode = (ChkDevMode.IsChecked == true);
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        // ================= Display Output Tab Handlers =================

        private void RefreshMonitorsList()
        {
            var displays = MonitorHelper.GetDisplays();
            CmbMonitors.ItemsSource = displays;
        }

        private void SelectMatchingMonitor(int screenX, int screenY)
        {
            if (CmbMonitors.ItemsSource is System.Collections.Generic.List<DisplayInfo> displays)
            {
                foreach (var d in displays)
                {
                    if (d.Left == screenX && d.Top == screenY)
                    {
                        CmbMonitors.SelectedItem = d;
                        ScreenResolutionText.Text = $"Resolution: {d.Width} × {d.Height}";
                        ScreenOffsetText.Text = $"Desktop Offset: ({d.Left}, {d.Top})";
                        return;
                    }
                }
                if (displays.Count > 0)
                {
                    CmbMonitors.SelectedIndex = 0;
                }
            }
        }

        private void CmbMonitors_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbMonitors.SelectedItem is DisplayInfo display)
            {
                _model.ScreenX = display.Left;
                _model.ScreenY = display.Top;
                _model.ScreenWidth = display.Width;
                _model.ScreenHeight = display.Height;

                ScreenResolutionText.Text = $"Resolution: {display.Width} × {display.Height}";
                ScreenOffsetText.Text = $"Desktop Offset: ({display.Left}, {display.Top})";
                SendAndSaveConfig();
            }
        }

        private void BtnRefreshMonitors_Click(object sender, RoutedEventArgs e)
        {
            RefreshMonitorsList();
            SelectMatchingMonitor(_model.ScreenX, _model.ScreenY);
        }

        // ================= Overclock & Filter Handlers =================

        private void UpdateActiveRateBadge()
        {
            if (TxtActiveRateBadge == null) return;
            if (_model.InterpolationRate == 0)
            {
                TxtActiveRateBadge.Text = "Native";
                TxtActiveRateBadge.Foreground = (Brush)FindResource("TextSecondaryBrush");
            }
            else if (_model.InterpolationRate == 1000 || _model.InterpolationRate == 2000 ||
                     _model.InterpolationRate == 4000 || _model.InterpolationRate == 8000)
            {
                TxtActiveRateBadge.Text = $"{_model.InterpolationRate} Hz (Preset)";
                TxtActiveRateBadge.Foreground = (Brush)FindResource("AccentBrush");
            }
            else
            {
                TxtActiveRateBadge.Text = $"Custom ({_model.InterpolationRate} Hz)";
                TxtActiveRateBadge.Foreground = (Brush)FindResource("AccentBrush");
            }
        }

        private void CmbOverclockRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (CmbOverclockRate.SelectedItem is ComboBoxItem item)
            {
                if (item == CmbItemCustomRate)
                {
                    // Already in custom mode
                    return;
                }
                if (item.Tag is string tagStr && int.TryParse(tagStr, out int rate))
                {
                    _model.InterpolationRate = rate;
                    _model.IsModified = true;
                    if (CmbItemCustomRate != null)
                    {
                        CmbItemCustomRate.Visibility = Visibility.Collapsed;
                    }
                    if (TxtCustomInterpolation != null && !TxtCustomInterpolation.IsFocused)
                    {
                        TxtCustomInterpolation.Text = rate.ToString();
                    }
                    UpdateActiveRateBadge();
                    SendAndSaveConfig();
                }
            }
        }

        private void BtnApplyCustomInterpolation_Click(object sender, RoutedEventArgs e)
        {
            CommitCustomInterpolation();
        }

        private void ChkAntichatter_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            if (ChkAntichatter.IsChecked == true)
            {
                if (_model.AntichatterDeadzone == 0)
                {
                    _model.AntichatterDeadzone = 4;
                    if (SliderAntichatter != null) SliderAntichatter.Value = 4;
                }
            }
            else
            {
                _model.AntichatterDeadzone = 0;
                if (SliderAntichatter != null) SliderAntichatter.Value = 0;
            }
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        private void SliderAntichatter_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi) return;
            int val = (int)Math.Round(e.NewValue);
            _model.AntichatterDeadzone = val;
            if (ChkAntichatter != null)
            {
                ChkAntichatter.IsChecked = (val > 0);
            }
            if (TxtAntichatterVal != null)
            {
                TxtAntichatterVal.Text = (val == 0) ? "0 counts (Off)" : $"{val} counts (~{val * 0.01:F2} mm)";
            }
            if (SliderAntichatter != null)
            {
                SliderAntichatter.IsEnabled = (val > 0) || _model.DevMode;
            }
            _model.IsModified = true;
            SendAndSaveConfig();
        }

        private void ChkSmoothing_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.EnableSmoothing = (ChkSmoothing.IsChecked == true);
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        private void SliderSmoothing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi) return;
            int val = (int)Math.Round(e.NewValue);
            _model.SmoothingStrength = val;
            if (TxtSmoothingVal != null)
            {
                TxtSmoothingVal.Text = $"{val}%";
            }
            if (SliderSmoothing != null)
            {
                SliderSmoothing.IsEnabled = _model.EnableSmoothing || _model.DevMode;
            }
            _model.IsModified = true;
            SendAndSaveConfig();
        }

        private void ChkPrediction_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _model.EnablePrediction = (ChkPrediction.IsChecked == true);
            _model.IsModified = true;
            UpdateUiFromModel();
            SendAndSaveConfig();
        }

        private void SliderPrediction_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi) return;
            double val = Math.Round(e.NewValue, 1);
            _model.PredictionLookaheadMs = val;
            if (TxtPredictionVal != null)
            {
                TxtPredictionVal.Text = _model.EnablePrediction ? $"{val:0.#} ms" : "Off";
            }
            if (SliderPrediction != null)
            {
                SliderPrediction.IsEnabled = _model.EnablePrediction || _model.DevMode;
            }
            _model.IsModified = true;
            SendAndSaveConfig();
        }

        // ================= Dev Mode Manual Inputs =================

        private void TxtCustomInterpolation_LostFocus(object sender, RoutedEventArgs e) => CommitCustomInterpolation();
        private void TxtCustomInterpolation_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitCustomInterpolation();
                Keyboard.ClearFocus();
            }
        }

        private void CommitCustomInterpolation()
        {
            if (_isUpdatingUi || TxtCustomInterpolation == null) return;
            if (int.TryParse(TxtCustomInterpolation.Text.Trim(), out int rate) && rate >= 0)
            {
                _model.InterpolationRate = rate;
                _model.IsModified = true;
                UpdateUiFromModel();
                SendAndSaveConfig();
            }
        }

        private void TxtAntichatterManual_LostFocus(object sender, RoutedEventArgs e) => CommitAntichatterManual();
        private void TxtAntichatterManual_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitAntichatterManual();
                Keyboard.ClearFocus();
            }
        }

        private void CommitAntichatterManual()
        {
            if (_isUpdatingUi || TxtAntichatterManual == null) return;
            if (int.TryParse(TxtAntichatterManual.Text.Trim(), out int val) && val >= 0)
            {
                _model.AntichatterDeadzone = val;
                _model.IsModified = true;
                UpdateUiFromModel();
                SendAndSaveConfig();
            }
        }

        private void TxtSmoothingManual_LostFocus(object sender, RoutedEventArgs e) => CommitSmoothingManual();
        private void TxtSmoothingManual_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitSmoothingManual();
                Keyboard.ClearFocus();
            }
        }

        private void CommitSmoothingManual()
        {
            if (_isUpdatingUi || TxtSmoothingManual == null) return;
            if (int.TryParse(TxtSmoothingManual.Text.Trim(), out int val) && val >= 0)
            {
                _model.SmoothingStrength = val;
                _model.IsModified = true;
                UpdateUiFromModel();
                SendAndSaveConfig();
            }
        }

        private void TxtPredictionManual_LostFocus(object sender, RoutedEventArgs e) => CommitPredictionManual();
        private void TxtPredictionManual_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitPredictionManual();
                Keyboard.ClearFocus();
            }
        }

        private void CommitPredictionManual()
        {
            if (_isUpdatingUi || TxtPredictionManual == null) return;
            if (double.TryParse(TxtPredictionManual.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double val) && val >= 0)
            {
                _model.PredictionLookaheadMs = val;
                _model.IsModified = true;
                UpdateUiFromModel();
                SendAndSaveConfig();
            }
        }

        // ================= Telemetry Loop =================

        private void TelemetryTimer_Tick(object? sender, EventArgs e)
        {
            // If not connected to kernel driver, attempt reconnection every ~1 second (60 ticks)
            if (_driverInterop.State != DriverConnectionState.KernelModeConnected)
            {
                _reconnectCheckCounter++;
                if (_reconnectCheckCounter >= 60)
                {
                    _reconnectCheckCounter = 0;
                    if (_driverInterop.CheckAndReconnect())
                    {
                        UpdateDriverStatusUi();
                        _driverInterop.SendAreaConfig(_model);
                        _driverInterop.SendSettings(_model);
                    }
                }
            }

            var stats = _driverInterop.QueryStats(_model);

            // Responsive real-time PPS calculation with rolling fallback
            uint currentPps = stats.PacketsPerSecond;
            if (currentPps == 0)
            {
                if (stats.TotalPackets > _prevTotalPackets)
                {
                    double elapsed = _ppsStopwatch.Elapsed.TotalSeconds;
                    if (elapsed >= 0.15)
                    {
                        _calculatedPps = (uint)Math.Round((stats.TotalPackets - _prevTotalPackets) / elapsed);
                        _prevTotalPackets = stats.TotalPackets;
                        _ppsStopwatch.Restart();
                    }
                    currentPps = _calculatedPps;
                }
                else if (_ppsStopwatch.ElapsedMilliseconds > 350)
                {
                    _calculatedPps = 0;
                    currentPps = 0;
                }
            }
            else
            {
                _prevTotalPackets = stats.TotalPackets;
                _ppsStopwatch.Restart();
            }

            // Update live pen crosshair position on the tablet surface
            if (_model.ShowPenCursor && stats.InProximity != 0)
            {
                PenCrosshair.Visibility = Visibility.Visible;
                double canvasW = TabletCanvas?.ActualWidth > 0 ? TabletCanvas.ActualWidth : 760.0;
                double canvasH = TabletCanvas?.ActualHeight > 0 ? TabletCanvas.ActualHeight : 475.0;
                double maxX = Math.Max(1, _model.MaxRawX);
                double maxY = Math.Max(1, _model.MaxRawY);
                double px = ((double)stats.LastRawX / maxX) * canvasW - 12;
                double py = ((double)stats.LastRawY / maxY) * canvasH - 12;

                Canvas.SetLeft(PenCrosshair, Math.Clamp(px, 0, canvasW - 24));
                Canvas.SetTop(PenCrosshair, Math.Clamp(py, 0, canvasH - 24));
            }
            else
            {
                PenCrosshair.Visibility = Visibility.Collapsed;
            }
        }
    }
}
