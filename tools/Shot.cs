using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiquidUnzip
{
    /// <summary>截图工具：启动真实窗口，渲染后保存 PNG，用于视觉验证。</summary>
    public static class Shot
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main(string[] args)
        {
            SetProcessDPIAware();
            string outPath = args.Length > 0 ? args[0] : @"E:\我的项目\LiquidUnzip\docs\shot.png";
            string mode = args.Length > 1 ? args[1] : "idle";

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var w = new MainWindow();
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = 200; w.Top = 160;
            w.Topmost = true;

            w.Loaded += (s, e) =>
            {
                if (mode == "progress") w.ShowDemoProgress();
                else if (mode == "preview") w.ShowDemoPreview();
                else if (mode == "wide") w.ShowDemoWide();

                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
                t.Tick += (s2, e2) =>
                {
                    t.Stop();
                    w.Activate();
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    t2.Tick += (s3, e3) =>
                    {
                        t2.Stop();
                        try
                        {
                            // 关键：截图前冻结背景并解除"对截图隐身"，
                            // 否则窗口本身不会出现在截图里（那正是它透出桌面的证明）。
                            w.PrepareForCapture();
                            var t3 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                            t3.Tick += (s4, e4) =>
                            {
                                t3.Stop();
                                try { Save(w, outPath); }
                                catch (Exception ex) { Console.WriteLine("ERR " + ex.Message); }
                                app.Shutdown();
                            };
                            t3.Start();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("ERR " + ex.Message);
                            app.Shutdown();
                        }
                    };
                    t2.Start();
                };
                t.Start();
            };
            app.Run(w);
        }

        static void Save(Window w, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // 用真实的窗口矩形（考虑 DPI 缩放），而不是 WPF 的逻辑单位
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            RECT r;
            if (!GetWindowRect(hwnd, out r))
            {
                r = new RECT
                {
                    Left = (int)w.Left, Top = (int)w.Top,
                    Right = (int)(w.Left + w.ActualWidth),
                    Bottom = (int)(w.Top + w.ActualHeight)
                };
            }
            int cw = Math.Max(1, r.Right - r.Left);
            int ch = Math.Max(1, r.Bottom - r.Top);

            using (var bmp = new System.Drawing.Bitmap(cw, ch))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0,
                        new System.Drawing.Size(cw, ch), System.Drawing.CopyPixelOperation.SourceCopy);
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("SAVED " + path + " " + cw + "x" + ch + " @" + r.Left + "," + r.Top);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    }
}
