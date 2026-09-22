using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace osuKernel
{
    /// <summary>
    /// Material Design 3 (Material You) Dynamic Tonal Color Palette Engine.
    /// Extracts the system accent color from Windows DWM and computes MD3 color roles.
    /// </summary>
    public static class MaterialColorPalette
    {
        public const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        public const int WM_SETTINGCHANGE = 0x001A;

        [DllImport("dwmapi.dll", EntryPoint = "DwmGetColorizationColor")]
        private static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);

        public static Color SystemAccentColor { get; private set; } = Color.FromRgb(0, 120, 212);

        /// <summary>
        /// Reads the active Windows 10/11 system accent color.
        /// </summary>
        public static Color GetWindowsAccentColor()
        {
            try
            {
                // 1. Try Windows 10/11 DWM Registry (most accurate for Windows 11 Material You accent)
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key != null)
                {
                    object? val = key.GetValue("AccentColor");
                    if (val is int abgr)
                    {
                        // AccentColor is stored as ABGR: 0xAABBGGRR
                        byte a = (byte)((abgr >> 24) & 0xFF);
                        byte b = (byte)((abgr >> 16) & 0xFF);
                        byte g = (byte)((abgr >> 8) & 0xFF);
                        byte r = (byte)(abgr & 0xFF);
                        if (r != 0 || g != 0 || b != 0)
                        {
                            return Color.FromRgb(r, g, b);
                        }
                    }

                    val = key.GetValue("ColorizationColor");
                    if (val is int argb)
                    {
                        byte r = (byte)((argb >> 16) & 0xFF);
                        byte g = (byte)((argb >> 8) & 0xFF);
                        byte b = (byte)(argb & 0xFF);
                        if (r != 0 || g != 0 || b != 0)
                        {
                            return Color.FromRgb(r, g, b);
                        }
                    }
                }
            }
            catch { }

            try
            {
                // 2. Try DWM API
                if (DwmGetColorizationColor(out uint colorization, out _) == 0)
                {
                    byte r = (byte)((colorization >> 16) & 0xFF);
                    byte g = (byte)((colorization >> 8) & 0xFF);
                    byte b = (byte)(colorization & 0xFF);
                    if (r != 0 || g != 0 || b != 0)
                    {
                        return Color.FromRgb(r, g, b);
                    }
                }
            }
            catch { }

            try
            {
                // 3. Fallback to SystemParameters.WindowGlassColor
                var glass = SystemParameters.WindowGlassColor;
                if (glass.R != 0 || glass.G != 0 || glass.B != 0)
                {
                    return glass;
                }
            }
            catch { }

            // Default fallback Material Blue
            return Color.FromRgb(0x38, 0x8E, 0x3C); // Material Green/Blue fallback
        }

        /// <summary>
        /// Updates Application resources with dynamically computed MD3 tokens.
        /// </summary>
        public static void ApplyDynamicPalette(ResourceDictionary? targetResources = null)
        {
            var res = targetResources ?? Application.Current.Resources;
            if (res == null) return;

            Color primary = GetWindowsAccentColor();
            SystemAccentColor = primary;

            // Ensure good saturation/luminance for MD3 Dark theme
            primary = EnsureVibrancyForDarkTheme(primary);

            // Compute MD3 color roles
            Color onPrimary = GetPerceivedLuminance(primary) > 0.55 ? Color.FromRgb(10, 15, 20) : Color.FromRgb(255, 255, 255);
            Color primaryHover = Lighten(primary, 0.15);
            Color primaryPressed = Darken(primary, 0.15);
            Color primaryContainer = Blend(primary, Color.FromRgb(0x18, 0x1A, 0x20), 0.28);
            Color onPrimaryContainer = Lighten(primary, 0.40);
            Color primaryGlow = Color.FromArgb(60, primary.R, primary.G, primary.B);
            Color primarySubtle = Color.FromArgb(35, primary.R, primary.G, primary.B);

            // MD3 Neutral Surfaces
            Color surface = Color.FromRgb(0x11, 0x13, 0x18);
            Color surfaceContainerLowest = Color.FromRgb(0x0C, 0x0E, 0x12);
            Color surfaceContainerLow = Color.FromRgb(0x16, 0x18, 0x1E);
            Color surfaceContainer = Color.FromRgb(0x1C, 0x1E, 0x24);
            Color surfaceContainerHigh = Color.FromRgb(0x26, 0x28, 0x30);
            Color surfaceContainerHighest = Color.FromRgb(0x30, 0x33, 0x3C);

            // MD3 Outlines & Text
            Color outline = Color.FromRgb(0x43, 0x46, 0x51);
            Color outlineVariant = Color.FromRgb(0x2B, 0x2D, 0x36);
            Color onSurface = Color.FromRgb(0xFA, 0xFA, 0xFC);
            Color onSurfaceVariant = Color.FromRgb(0xC4, 0xC6, 0xD0);
            Color textTertiary = Color.FromRgb(0x8E, 0x90, 0x99);

            // Functional Colors
            Color success = Color.FromRgb(0x4A, 0xDE, 0x80);
            Color warning = Color.FromRgb(0xFB, 0xBF, 0x24);
            Color danger = Color.FromRgb(0xF8, 0x71, 0x71);

            // Update Colors in ResourceDictionary
            SetResource(res, "AccentColor", primary);
            SetResource(res, "AccentHoverColor", primaryHover);
            SetResource(res, "AccentPressedColor", primaryPressed);
            SetResource(res, "AccentSubtleColor", primarySubtle);
            SetResource(res, "AccentGlowColor", primaryGlow);
            SetResource(res, "PrimaryContainerColor", primaryContainer);
            SetResource(res, "OnPrimaryContainerColor", onPrimaryContainer);
            SetResource(res, "OnPrimaryColor", onPrimary);

            SetResource(res, "MicaBackgroundColor", surface);
            SetResource(res, "CardBackgroundColor", surfaceContainer);
            SetResource(res, "CardSubtleColor", surfaceContainerLow);
            SetResource(res, "CardHoverColor", surfaceContainerHigh);
            SetResource(res, "CardBorderColor", outlineVariant);
            SetResource(res, "OutlineColor", outline);

            SetResource(res, "TextPrimaryColor", onSurface);
            SetResource(res, "TextSecondaryColor", onSurfaceVariant);
            SetResource(res, "TextTertiaryColor", textTertiary);

            // Update Brushes in ResourceDictionary
            SetResource(res, "AccentBrush", new SolidColorBrush(primary));
            SetResource(res, "AccentHoverBrush", new SolidColorBrush(primaryHover));
            SetResource(res, "AccentPressedBrush", new SolidColorBrush(primaryPressed));
            SetResource(res, "AccentSubtleBrush", new SolidColorBrush(primarySubtle));
            SetResource(res, "PrimaryContainerBrush", new SolidColorBrush(primaryContainer));
            SetResource(res, "OnPrimaryContainerBrush", new SolidColorBrush(onPrimaryContainer));
            SetResource(res, "OnPrimaryBrush", new SolidColorBrush(onPrimary));

            SetResource(res, "MicaBackgroundBrush", new SolidColorBrush(surface));
            SetResource(res, "CardBackgroundBrush", new SolidColorBrush(surfaceContainer));
            SetResource(res, "CardSubtleBrush", new SolidColorBrush(surfaceContainerLow));
            SetResource(res, "CardHoverBrush", new SolidColorBrush(surfaceContainerHigh));
            SetResource(res, "CardBorderBrush", new SolidColorBrush(outlineVariant));
            SetResource(res, "OutlineBrush", new SolidColorBrush(outline));

            SetResource(res, "TextPrimaryBrush", new SolidColorBrush(onSurface));
            SetResource(res, "TextSecondaryBrush", new SolidColorBrush(onSurfaceVariant));
            SetResource(res, "TextTertiaryBrush", new SolidColorBrush(textTertiary));

            SetResource(res, "SuccessGreenBrush", new SolidColorBrush(success));
            SetResource(res, "WarningAmberBrush", new SolidColorBrush(warning));
            SetResource(res, "DangerRedBrush", new SolidColorBrush(danger));
        }

        private static void SetResource(ResourceDictionary res, string key, object value)
        {
            if (res.Contains(key))
            {
                res[key] = value;
            }
            else
            {
                res.Add(key, value);
            }
        }

        private static double GetPerceivedLuminance(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        private static Color EnsureVibrancyForDarkTheme(Color c)
        {
            double lum = GetPerceivedLuminance(c);
            if (lum < 0.28)
            {
                // Too dark for dark background, lighten it
                return Lighten(c, 0.35);
            }
            return c;
        }

        private static Color Lighten(Color c, double factor)
        {
            byte r = (byte)Math.Min(255, c.R + (255 - c.R) * factor);
            byte g = (byte)Math.Min(255, c.G + (255 - c.G) * factor);
            byte b = (byte)Math.Min(255, c.B + (255 - c.B) * factor);
            return Color.FromRgb(r, g, b);
        }

        private static Color Darken(Color c, double factor)
        {
            byte r = (byte)Math.Max(0, c.R * (1.0 - factor));
            byte g = (byte)Math.Max(0, c.G * (1.0 - factor));
            byte b = (byte)Math.Max(0, c.B * (1.0 - factor));
            return Color.FromRgb(r, g, b);
        }

        private static Color Blend(Color foreground, Color background, double alpha)
        {
            byte r = (byte)(foreground.R * alpha + background.R * (1.0 - alpha));
            byte g = (byte)(foreground.G * alpha + background.G * (1.0 - alpha));
            byte b = (byte)(foreground.B * alpha + background.B * (1.0 - alpha));
            return Color.FromRgb(r, g, b);
        }
    }
}
