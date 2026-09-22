using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace osuKernel
{
    public class TabletAreaModel : INotifyPropertyChanged
    {
        // Default physical constants
        public const double DefaultPhysicalWidthMm = 152.0;
        public const double DefaultPhysicalHeightMm = 95.0;
        public const int DefaultMaxRawX = 15200;
        public const int DefaultMaxRawY = 9500;
        public const double DefaultCountsPerMm = 100.0;

        private TabletSpecification _activeTablet = TabletDatabase.DefaultTablet;
        private string _selectedTabletModel = "Auto";

        public TabletSpecification ActiveTablet
        {
            get => _activeTablet;
            set
            {
                if (_activeTablet != value && value != null)
                {
                    _activeTablet = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PhysicalWidthMm));
                    OnPropertyChanged(nameof(PhysicalHeightMm));
                    OnPropertyChanged(nameof(MaxRawX));
                    OnPropertyChanged(nameof(MaxRawY));
                    OnPropertyChanged(nameof(CountsPerMm));
                    ClampCenter();
                    NotifyAllAreaProperties();
                }
            }
        }

        public string SelectedTabletModel
        {
            get => _selectedTabletModel;
            set
            {
                if (_selectedTabletModel != value)
                {
                    _selectedTabletModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public double PhysicalWidthMm => _activeTablet.WidthMm;
        public double PhysicalHeightMm => _activeTablet.HeightMm;
        public int MaxRawX => _activeTablet.MaxX;
        public int MaxRawY => _activeTablet.MaxY;
        public double CountsPerMm => _activeTablet.CountsPerMm;

        private double _widthMm = 152.0;
        private double _heightMm = 95.0;
        private double _xMm = 76.0; // Center X in mm (matching OpenTabletDriver)
        private double _yMm = 47.5; // Center Y in mm (matching OpenTabletDriver)

        private int _screenX = 0;
        private int _screenY = 0;
        private int _screenWidth = 1920;
        private int _screenHeight = 1080;

        private bool _lockAspectRatio = false;
        private bool _areaClipping = true;
        private bool _areaLimiting = false;
        private int _rotation = 0;

        // Latency & Pen Tip
        private bool _lowLatencyKernelMode = true;
        private bool _enableTipClick = true;

        // Smoothing, Antichatter & 8kHz Overclock / Interpolation
        private bool _enableSmoothing = false;
        private int _smoothingStrength = 25;
        private int _antichatterDeadzone = 0;
        private int _interpolationRate = 0; // 0 = Native, 1000, 2000, 4000, 8000
        private bool _enablePrediction = false;
        private double _predictionLookaheadMs = 1.5;

        // Visualizer Elements Display
        private bool _showAreaHandles = true;
        private bool _showPenCursor = true;

        public bool ShowAreaHandles
        {
            get => _showAreaHandles;
            set
            {
                if (_showAreaHandles != value)
                {
                    _showAreaHandles = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowPenCursor
        {
            get => _showPenCursor;
            set
            {
                if (_showPenCursor != value)
                {
                    _showPenCursor = value;
                    OnPropertyChanged();
                }
            }
        }

        // Aspect Ratio Preset & Custom Ratios
        private string _aspectRatioPreset = "Display"; // "Display", "16:9", "16:10", "4:3", "1:1", "Custom"
        private double _customRatioX = 16.0;
        private double _customRatioY = 9.0;

        public string AspectRatioPreset
        {
            get => _aspectRatioPreset;
            set
            {
                if (_aspectRatioPreset != value)
                {
                    _aspectRatioPreset = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TargetAspectRatio));
                    if (_lockAspectRatio) UpdateHeightForAspectRatio();
                }
            }
        }

        public double CustomRatioX
        {
            get => _customRatioX;
            set
            {
                if (Math.Abs(_customRatioX - value) > 0.001 && value > 0)
                {
                    _customRatioX = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TargetAspectRatio));
                    if (_lockAspectRatio) UpdateHeightForAspectRatio();
                }
            }
        }

        public double CustomRatioY
        {
            get => _customRatioY;
            set
            {
                if (Math.Abs(_customRatioY - value) > 0.001 && value > 0)
                {
                    _customRatioY = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TargetAspectRatio));
                    if (_lockAspectRatio) UpdateHeightForAspectRatio();
                }
            }
        }

        public double TargetAspectRatio
        {
            get
            {
                return _aspectRatioPreset switch
                {
                    "16:9" => 16.0 / 9.0,
                    "16:10" => 16.0 / 10.0,
                    "4:3" => 4.0 / 3.0,
                    "1:1" => 1.0,
                    "Custom" => (_customRatioY > 0) ? (_customRatioX / _customRatioY) : (16.0 / 9.0),
                    _ => (_screenHeight > 0) ? ((double)_screenWidth / _screenHeight) : (16.0 / 9.0)
                };
            }
        }

        // Active preset tracking
        private string _activePresetName = "Active Session";
        private bool _isModified = false;

        public string ActivePresetName
        {
            get => _activePresetName;
            set { _activePresetName = value; OnPropertyChanged(); }
        }

        public bool IsModified
        {
            get => _isModified;
            set { _isModified = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public bool DevMode
        {
            get => ProfileManager.DevMode;
            set
            {
                if (ProfileManager.DevMode != value)
                {
                    ProfileManager.DevMode = value;
                    OnPropertyChanged();
                    if (!value)
                    {
                        ClampCenter();
                        if (_rotation > 180 || _rotation < -180)
                        {
                            int rot = _rotation;
                            while (rot > 180) rot -= 360;
                            while (rot < -180) rot += 360;
                            Rotation = rot;
                        }
                    }
                    NotifyAllAreaProperties();
                }
            }
        }

        public void ClampCenter()
        {
            if (DevMode) return;

            double halfW = _widthMm / 2.0;
            double minX = halfW;
            double maxX = PhysicalWidthMm - halfW;
            _xMm = (maxX >= minX) ? Math.Clamp(_xMm, minX, maxX) : halfW;

            double halfH = _heightMm / 2.0;
            double minY = halfH;
            double maxY = PhysicalHeightMm - halfH;
            _yMm = (maxY >= minY) ? Math.Clamp(_yMm, minY, maxY) : halfH;
        }

        public double WidthMm
        {
            get => _widthMm;
            set
            {
                double val = DevMode ? Math.Max(0.1, value) : Math.Clamp(value, 5.0, PhysicalWidthMm);
                if (Math.Abs(_widthMm - val) > 0.001)
                {
                    _widthMm = val;
                    if (_lockAspectRatio)
                    {
                        double ratio = TargetAspectRatio;
                        _heightMm = DevMode ? (_widthMm / ratio) : Math.Clamp(_widthMm / ratio, 5.0, PhysicalHeightMm);
                        OnPropertyChanged(nameof(HeightMm));
                        OnPropertyChanged(nameof(HeightCounts));
                    }
                    ClampCenter();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WidthCounts));
                    OnPropertyChanged(nameof(XMm));
                    OnPropertyChanged(nameof(YMm));
                    OnPropertyChanged(nameof(LeftMm));
                    OnPropertyChanged(nameof(TopMm));
                    OnPropertyChanged(nameof(XCounts));
                    OnPropertyChanged(nameof(YCounts));
                }
            }
        }

        public double HeightMm
        {
            get => _heightMm;
            set
            {
                double val = DevMode ? Math.Max(0.1, value) : Math.Clamp(value, 5.0, PhysicalHeightMm);
                if (Math.Abs(_heightMm - val) > 0.001)
                {
                    _heightMm = val;
                    if (_lockAspectRatio)
                    {
                        double ratio = TargetAspectRatio;
                        _widthMm = DevMode ? (_heightMm * ratio) : Math.Clamp(_heightMm * ratio, 5.0, PhysicalWidthMm);
                        OnPropertyChanged(nameof(WidthMm));
                        OnPropertyChanged(nameof(WidthCounts));
                    }
                    ClampCenter();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HeightCounts));
                    OnPropertyChanged(nameof(XMm));
                    OnPropertyChanged(nameof(YMm));
                    OnPropertyChanged(nameof(LeftMm));
                    OnPropertyChanged(nameof(TopMm));
                    OnPropertyChanged(nameof(XCounts));
                    OnPropertyChanged(nameof(YCounts));
                }
            }
        }

        /// <summary>
        /// Center X position in mm (matches OpenTabletDriver convention exactly).
        /// In standard mode: Range Width/2 to 152 - Width/2.
        /// In DevMode: completely unrestricted.
        /// </summary>
        public double XMm
        {
            get => _xMm;
            set
            {
                double val;
                if (DevMode)
                {
                    val = value;
                }
                else
                {
                    double halfW = _widthMm / 2.0;
                    double minX = halfW;
                    double maxX = PhysicalWidthMm - halfW;
                    val = (maxX >= minX) ? Math.Clamp(value, minX, maxX) : halfW;
                }

                if (Math.Abs(_xMm - val) > 0.001)
                {
                    _xMm = val;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LeftMm));
                    OnPropertyChanged(nameof(XCounts));
                }
            }
        }

        /// <summary>
        /// Center Y position in mm (matches OpenTabletDriver convention exactly).
        /// In standard mode: Range Height/2 to 95 - Height/2.
        /// In DevMode: completely unrestricted.
        /// </summary>
        public double YMm
        {
            get => _yMm;
            set
            {
                double val;
                if (DevMode)
                {
                    val = value;
                }
                else
                {
                    double halfH = _heightMm / 2.0;
                    double minY = halfH;
                    double maxY = PhysicalHeightMm - halfH;
                    val = (maxY >= minY) ? Math.Clamp(value, minY, maxY) : halfH;
                }

                if (Math.Abs(_yMm - val) > 0.001)
                {
                    _yMm = val;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TopMm));
                    OnPropertyChanged(nameof(YCounts));
                }
            }
        }

        /// <summary>
        /// Top-left X corner in millimeters.
        /// </summary>
        public double LeftMm => DevMode ? (_xMm - (_widthMm / 2.0)) : Math.Clamp(_xMm - (_widthMm / 2.0), 0.0, Math.Max(0.0, PhysicalWidthMm - _widthMm));

        /// <summary>
        /// Top-left Y corner in millimeters.
        /// </summary>
        public double TopMm => DevMode ? (_yMm - (_heightMm / 2.0)) : Math.Clamp(_yMm - (_heightMm / 2.0), 0.0, Math.Max(0.0, PhysicalHeightMm - _heightMm));

        // Raw Counts representations for kernel driver (top-left offset and dimensions)
        public int WidthCounts => (int)Math.Round(WidthMm * CountsPerMm);
        public int HeightCounts => (int)Math.Round(HeightMm * CountsPerMm);
        public int XCounts => (int)Math.Round(LeftMm * CountsPerMm);
        public int YCounts => (int)Math.Round(TopMm * CountsPerMm);

        public int ScreenX
        {
            get => _screenX;
            set { _screenX = value; OnPropertyChanged(); }
        }

        public int ScreenY
        {
            get => _screenY;
            set { _screenY = value; OnPropertyChanged(); }
        }

        public int ScreenWidth
        {
            get => _screenWidth;
            set
            {
                if (_screenWidth != value && value > 0)
                {
                    _screenWidth = value;
                    OnPropertyChanged();
                    if (_lockAspectRatio) UpdateHeightForAspectRatio();
                }
            }
        }

        public int ScreenHeight
        {
            get => _screenHeight;
            set
            {
                if (_screenHeight != value && value > 0)
                {
                    _screenHeight = value;
                    OnPropertyChanged();
                    if (_lockAspectRatio) UpdateHeightForAspectRatio();
                }
            }
        }

        public bool LockAspectRatio
        {
            get => _lockAspectRatio;
            set
            {
                _lockAspectRatio = value;
                OnPropertyChanged();
                if (value) UpdateHeightForAspectRatio();
            }
        }

        public bool AreaClipping
        {
            get => _areaClipping;
            set { _areaClipping = value; OnPropertyChanged(); }
        }

        public bool AreaLimiting
        {
            get => _areaLimiting;
            set { _areaLimiting = value; OnPropertyChanged(); }
        }

        public int Rotation
        {
            get => _rotation;
            set
            {
                int val = DevMode ? value : Math.Clamp(value, -180, 180);
                if (_rotation != val)
                {
                    _rotation = val;
                    OnPropertyChanged();
                }
            }
        }

        public bool LowLatencyKernelMode
        {
            get => _lowLatencyKernelMode;
            set { _lowLatencyKernelMode = value; OnPropertyChanged(); }
        }

        public bool EnableTipClick
        {
            get => _enableTipClick;
            set { _enableTipClick = value; OnPropertyChanged(); }
        }

        public bool EnableSmoothing
        {
            get => _enableSmoothing;
            set { _enableSmoothing = value; OnPropertyChanged(); }
        }

        public int SmoothingStrength
        {
            get => _smoothingStrength;
            set { _smoothingStrength = value; OnPropertyChanged(); }
        }

        public int AntichatterDeadzone
        {
            get => _antichatterDeadzone;
            set { _antichatterDeadzone = value; OnPropertyChanged(); }
        }

        public int InterpolationRate
        {
            get => _interpolationRate;
            set { _interpolationRate = value; OnPropertyChanged(); }
        }

        public bool EnablePrediction
        {
            get => _enablePrediction;
            set { _enablePrediction = value; OnPropertyChanged(); }
        }

        public double PredictionLookaheadMs
        {
            get => _predictionLookaheadMs;
            set { _predictionLookaheadMs = value; OnPropertyChanged(); }
        }

        public void UpdateHeightForAspectRatio()
        {
            double ratio = TargetAspectRatio;
            double targetH = _widthMm / ratio;
            if (!DevMode && targetH > PhysicalHeightMm)
            {
                _heightMm = PhysicalHeightMm;
                _widthMm = _heightMm * ratio;
            }
            else
            {
                _heightMm = targetH;
            }
            ClampCenter();
            OnPropertyChanged(nameof(WidthMm));
            OnPropertyChanged(nameof(HeightMm));
            OnPropertyChanged(nameof(WidthCounts));
            OnPropertyChanged(nameof(HeightCounts));
            OnPropertyChanged(nameof(XMm));
            OnPropertyChanged(nameof(YMm));
            OnPropertyChanged(nameof(LeftMm));
            OnPropertyChanged(nameof(TopMm));
            OnPropertyChanged(nameof(XCounts));
            OnPropertyChanged(nameof(YCounts));
        }

        public void SetFullArea()
        {
            _widthMm = PhysicalWidthMm;
            _heightMm = PhysicalHeightMm;
            _xMm = PhysicalWidthMm / 2.0;
            _yMm = PhysicalHeightMm / 2.0;
            NotifyAllAreaProperties();
        }

        public void CenterArea()
        {
            _xMm = PhysicalWidthMm / 2.0;
            _yMm = PhysicalHeightMm / 2.0;
            NotifyAllAreaProperties();
        }

        public void SetAreaDimensions(double width, double height, bool center = true)
        {
            _widthMm = DevMode ? Math.Max(0.1, width) : Math.Clamp(width, 5.0, PhysicalWidthMm);
            _heightMm = DevMode ? Math.Max(0.1, height) : Math.Clamp(height, 5.0, PhysicalHeightMm);
            if (center)
            {
                _xMm = PhysicalWidthMm / 2.0;
                _yMm = PhysicalHeightMm / 2.0;
            }
            else
            {
                ClampCenter();
            }
            NotifyAllAreaProperties();
        }

        public void MatchDisplayRatio()
        {
            _lockAspectRatio = true;
            OnPropertyChanged(nameof(LockAspectRatio));
            double ratio = TargetAspectRatio;
            double targetH = _widthMm / ratio;
            if (!DevMode && targetH > PhysicalHeightMm)
            {
                _heightMm = PhysicalHeightMm;
                _widthMm = _heightMm * ratio;
            }
            else
            {
                _heightMm = targetH;
            }
            CenterArea();
            NotifyAllAreaProperties();
        }

        public void SetOsuSmall()
        {
            SetAreaDimensions(70.0, 43.75, center: true);
        }

        public void SetOsuMedium()
        {
            SetAreaDimensions(90.0, 56.25, center: true);
        }

        public void SetOsuLarge()
        {
            SetAreaDimensions(110.0, 68.75, center: true);
        }

        private void NotifyAllAreaProperties()
        {
            OnPropertyChanged(nameof(DevMode));
            OnPropertyChanged(nameof(XMm));
            OnPropertyChanged(nameof(YMm));
            OnPropertyChanged(nameof(LeftMm));
            OnPropertyChanged(nameof(TopMm));
            OnPropertyChanged(nameof(WidthMm));
            OnPropertyChanged(nameof(HeightMm));
            OnPropertyChanged(nameof(XCounts));
            OnPropertyChanged(nameof(YCounts));
            OnPropertyChanged(nameof(WidthCounts));
            OnPropertyChanged(nameof(HeightCounts));
            OnPropertyChanged(nameof(Rotation));
            OnPropertyChanged(nameof(LockAspectRatio));
            OnPropertyChanged(nameof(AreaClipping));
            OnPropertyChanged(nameof(AreaLimiting));
            OnPropertyChanged(nameof(LowLatencyKernelMode));
            OnPropertyChanged(nameof(EnableTipClick));
            OnPropertyChanged(nameof(ShowAreaHandles));
        }

        public void ApplyProfile(TabletProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.TabletModel))
            {
                _selectedTabletModel = profile.TabletModel;
                if (string.Equals(profile.TabletModel, "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    _activeTablet = TabletDatabase.DetectConnectedTablet();
                }
                else
                {
                    _activeTablet = TabletDatabase.GetByModelId(profile.TabletModel);
                }
                OnPropertyChanged(nameof(SelectedTabletModel));
                OnPropertyChanged(nameof(ActiveTablet));
                OnPropertyChanged(nameof(PhysicalWidthMm));
                OnPropertyChanged(nameof(PhysicalHeightMm));
                OnPropertyChanged(nameof(MaxRawX));
                OnPropertyChanged(nameof(MaxRawY));
                OnPropertyChanged(nameof(CountsPerMm));
            }

            _widthMm = DevMode ? Math.Max(0.1, profile.WidthMm) : Math.Clamp(profile.WidthMm, 5.0, PhysicalWidthMm);
            _heightMm = DevMode ? Math.Max(0.1, profile.HeightMm) : Math.Clamp(profile.HeightMm, 5.0, PhysicalHeightMm);

            double x = profile.XMm;
            double y = profile.YMm;

            if (DevMode)
            {
                _xMm = x;
                _yMm = y;
            }
            else
            {
                double halfW = _widthMm / 2.0;
                double halfH = _heightMm / 2.0;
                _xMm = Math.Clamp(x, halfW, Math.Max(halfW, PhysicalWidthMm - halfW));
                _yMm = Math.Clamp(y, halfH, Math.Max(halfH, PhysicalHeightMm - halfH));
            }

            _lockAspectRatio = profile.LockAspectRatio;
            _areaClipping = profile.AreaClipping;
            _areaLimiting = profile.AreaLimiting;
            int rot = profile.Rotation;
            if (!DevMode)
            {
                while (rot > 180) rot -= 360;
                while (rot < -180) rot += 360;
            }
            _rotation = rot;
            _lowLatencyKernelMode = profile.LowLatencyKernelMode;
            _enableTipClick = profile.EnableTipClick;
            _showAreaHandles = profile.ShowAreaHandles;

            _enableSmoothing = profile.EnableSmoothing;
            _smoothingStrength = profile.SmoothingStrength;
            _antichatterDeadzone = profile.AntichatterDeadzone;
            _interpolationRate = profile.InterpolationRate;
            _enablePrediction = profile.EnablePrediction;
            _predictionLookaheadMs = profile.PredictionLookaheadMs > 0 ? profile.PredictionLookaheadMs : 1.5;
            _showPenCursor = profile.ShowPenCursor;

            OnPropertyChanged(nameof(EnableSmoothing));
            OnPropertyChanged(nameof(SmoothingStrength));
            OnPropertyChanged(nameof(AntichatterDeadzone));
            OnPropertyChanged(nameof(InterpolationRate));
            OnPropertyChanged(nameof(EnablePrediction));
            OnPropertyChanged(nameof(PredictionLookaheadMs));
            OnPropertyChanged(nameof(ShowAreaHandles));
            OnPropertyChanged(nameof(ShowPenCursor));

            if (!string.IsNullOrWhiteSpace(profile.AspectRatioPreset))
            {
                _aspectRatioPreset = profile.AspectRatioPreset;
            }
            if (profile.CustomRatioX > 0) _customRatioX = profile.CustomRatioX;
            if (profile.CustomRatioY > 0) _customRatioY = profile.CustomRatioY;
            OnPropertyChanged(nameof(AspectRatioPreset));
            OnPropertyChanged(nameof(CustomRatioX));
            OnPropertyChanged(nameof(CustomRatioY));
            OnPropertyChanged(nameof(TargetAspectRatio));

            if (profile.ScreenWidth > 0 && profile.ScreenHeight > 0)
            {
                _screenX = profile.ScreenX;
                _screenY = profile.ScreenY;
                _screenWidth = profile.ScreenWidth;
                _screenHeight = profile.ScreenHeight;
                OnPropertyChanged(nameof(ScreenX));
                OnPropertyChanged(nameof(ScreenY));
                OnPropertyChanged(nameof(ScreenWidth));
                OnPropertyChanged(nameof(ScreenHeight));
            }

            ActivePresetName = string.IsNullOrWhiteSpace(profile.Name) ? "Active Session" : profile.Name;
            IsModified = false;

            NotifyAllAreaProperties();
        }

        public TabletProfile ToProfile(string? name = null)
        {
            return new TabletProfile
            {
                Name = name ?? ActivePresetName,
                WidthMm = WidthMm,
                HeightMm = HeightMm,
                XMm = XMm,
                YMm = YMm,
                LockAspectRatio = LockAspectRatio,
                AreaClipping = AreaClipping,
                AreaLimiting = AreaLimiting,
                Rotation = Rotation,
                LowLatencyKernelMode = LowLatencyKernelMode,
                EnableTipClick = EnableTipClick,
                ShowAreaHandles = ShowAreaHandles,
                ShowPenCursor = ShowPenCursor,
                EnableSmoothing = EnableSmoothing,
                SmoothingStrength = SmoothingStrength,
                AntichatterDeadzone = AntichatterDeadzone,
                InterpolationRate = InterpolationRate,
                EnablePrediction = EnablePrediction,
                PredictionLookaheadMs = PredictionLookaheadMs,
                AspectRatioPreset = AspectRatioPreset,
                CustomRatioX = CustomRatioX,
                CustomRatioY = CustomRatioY,
                ScreenX = ScreenX,
                ScreenY = ScreenY,
                ScreenWidth = ScreenWidth,
                ScreenHeight = ScreenHeight,
                TabletModel = _selectedTabletModel
            };
        }
    }
}
