using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LiquidUnzip
{
    /// <summary>
    /// 液态玻璃：DWM Acrylic 窗口背板 + 玻璃画刷工厂。
    /// 复用自 ControlCenter-Reverse/app/Glass.cs，针对解压工具做了精简与增强。
    /// </summary>
    public static class Glass
    {
        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData
        {
            public int Attribute; public IntPtr Data; public int SizeOfData;
        }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int l, r, t, b; }

        const int WCA_ACCENT_POLICY = 19;
        const int ACCENT_ENABLE_BLURBEHIND = 3;
        const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        /// <summary>
        /// 让窗口真正透出桌面：DWM 扩展到客户区 + Acrylic 背板。
        /// 关键点是 AllowsTransparency=false（硬件合成才能真正透视），
        /// 圆角由 Win11 的 DWM 负责。
        /// </summary>
        public static void EnableDesktopGlass(Window w, uint tint)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            try
            {
                int dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                // Win11：圆角偏好 2 = 圆角
                int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                // 整个客户区都用玻璃
                var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch { }

            try
            {
                var accent = new AccentPolicy
                {
                    AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                    GradientColor = unchecked((int)tint),
                    AccentFlags = 2,
                    AnimationId = 0
                };
                int sz = Marshal.SizeOf(accent);
                IntPtr ptr = Marshal.AllocHGlobal(sz);
                Marshal.StructureToPtr(accent, ptr, false);
                var d = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = sz
                };
                SetWindowCompositionAttribute(hwnd, ref d);
                Marshal.FreeHGlobal(ptr);
            }
            catch { }
        }

        /// <summary>启用窗口级液态玻璃背板。tint 为 AABBGGRR。</summary>
        public static void EnableAcrylic(Window w, uint tint, bool acrylic)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                // Win11 圆角 + 深色标题栏
                int dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch { }
            try
            {
                var accent = new AccentPolicy
                {
                    AccentState = acrylic ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND,
                    GradientColor = unchecked((int)tint),
                    AccentFlags = 2,
                    AnimationId = 0
                };
                int sz = Marshal.SizeOf(accent);
                IntPtr ptr = Marshal.AllocHGlobal(sz);
                Marshal.StructureToPtr(accent, ptr, false);
                var d = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = sz };
                SetWindowCompositionAttribute(hwnd, ref d);
                Marshal.FreeHGlobal(ptr);
            }
            catch { }
        }

        /// <summary>关闭玻璃：把 AccentState 设回禁用。</summary>
        public static void DisableGlass(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var accent = new AccentPolicy
                {
                    AccentState = 0,   // ACCENT_DISABLED
                    AccentFlags = 0,
                    GradientColor = 0,
                    AnimationId = 0
                };
                int sz = Marshal.SizeOf(accent);
                IntPtr ptr = Marshal.AllocHGlobal(sz);
                Marshal.StructureToPtr(accent, ptr, false);
                var d = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = sz
                };
                SetWindowCompositionAttribute(hwnd, ref d);
                Marshal.FreeHGlobal(ptr);

                // 收回扩展到客户区的边框
                var m = new MARGINS { l = 0, r = 0, t = 0, b = 0 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch { }
        }

        // ---- 画刷工厂 ----

        public static SolidColorBrush S(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        public static SolidColorBrush SA(Color c, byte a)
        {
            c.A = a; var b = new SolidColorBrush(c); b.Freeze(); return b;
        }

        /// <summary>玻璃面板底色：上亮下暗的极淡白。</summary>
        public static LinearGradientBrush GlassFill(double topAlpha, double bottomAlpha)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * topAlpha), 255, 255, 255), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * bottomAlpha), 255, 255, 255), 1));
            b.Freeze(); return b;
        }

        /// <summary>边缘高光：左上到右下的白→透明。</summary>
        public static LinearGradientBrush SpecularEdge(double a0, double a1, double a2)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * a0), 255, 255, 255), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * a1), 255, 255, 255), 0.5));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * a2), 255, 255, 255), 1));
            b.Freeze(); return b;
        }

        public static DropShadowEffect SoftShadow(double blur, double opacity, double depth)
        {
            var e = new DropShadowEffect
            {
                BlurRadius = blur, ShadowDepth = depth, Opacity = opacity,
                Color = Colors.Black, Direction = 270, RenderingBias = RenderingBias.Quality
            };
            e.Freeze(); return e;
        }

        public static DropShadowEffect Glow(Color c, double blur, double opacity)
        {
            var e = new DropShadowEffect
            {
                BlurRadius = blur, ShadowDepth = 0, Opacity = opacity,
                Color = c, RenderingBias = RenderingBias.Quality
            };
            e.Freeze(); return e;
        }

        /// <summary>玻璃风格滑杆用：轻描边阴影，让文字在任何壁纸上都可读。</summary>
        public static DropShadowEffect TextShadow()
        {
            var e = new DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.85,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Quality
            };
            e.Freeze();
            return e;
        }

        /// <summary>进度环用：线性渐变描边。</summary>
        public static LinearGradientBrush RingGradient(Color from, Color to)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            b.GradientStops.Add(new GradientStop(from, 0));
            b.GradientStops.Add(new GradientStop(to, 1));
            b.Freeze(); return b;
        }
    }

    /// <summary>Apple 深色系统色板。</summary>
    public static class Theme
    {
        public static readonly Color Blue = C("#0A84FF");
        public static readonly Color Sky = C("#2997FF");
        public static readonly Color Cyan = C("#64D2FF");
        public static readonly Color Indigo = C("#5E5CE6");
        public static readonly Color Mint = C("#32D74B");
        public static readonly Color Pink = C("#FF375F");
        public static readonly Color Orange = C("#FF9F0A");
        public static readonly Color Red = C("#FF453A");

        public static readonly Color TextPrimary = C("#FFFFFF");
        public static readonly Color TextSecondary = C("#AEAEB6");
        public static readonly Color TextTertiary = C("#6E6E78");

        public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
    }

    /// <summary>鼠标跟随的液态高光：给任意元素注入一层跟着指针走的柔光。</summary>
    public static class LiquidLight
    {
        public static void Attach(FrameworkElement host, FrameworkElement lightLayer, Point rest)
        {
            host.MouseMove += (s, e) =>
            {
                var p = e.GetPosition(host);
                double w = host.ActualWidth, h = host.ActualHeight;
                if (w <= 0 || h <= 0) return;
                var rg = lightLayer.OpacityMask as RadialGradientBrush;
                if (rg == null) return;
                rg.Center = new Point(p.X / w, p.Y / h);
                rg.GradientOrigin = new Point(p.X / w, p.Y / h);
            };
            host.MouseLeave += (s, e) =>
            {
                var rg = lightLayer.OpacityMask as RadialGradientBrush;
                if (rg == null) return;
                rg.Center = rest;
                rg.GradientOrigin = rest;
            };
        }
    }
}
