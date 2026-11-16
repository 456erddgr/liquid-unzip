using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace LiquidUnzip
{
    /// <summary>
    /// 真实流程验证：启动真窗口 → 拖入真实压缩包 → 走完整预览流程 → 截图。
    /// 不使用任何演示数据，验证的是用户实际会看到的界面。
    /// </summary>
    public static class TestReal
    {
        [STAThread]
        public static void Main(string[] args)
        {
            string archive = args.Length > 0 ? args[0] : @"E:\我的项目\LiquidUnzip\testdata\sample.zip";
            string outDir = args.Length > 1 ? args[1] : @"E:\我的项目\LiquidUnzip\docs";

            if (!File.Exists(archive))
            {
                Console.WriteLine("找不到压缩包: " + archive);
                Environment.Exit(2);
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var w = new MainWindow();
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = 240; w.Top = 160;
            w.Topmost = true;

            w.Loaded += (s, e) =>
            {
                // 走真实入口：和用户拖入文件完全同一条代码路径
                w.BeginExtractFromArgs(archive);

                // 等预览树加载完再截图
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
                t.Tick += (s2, e2) =>
                {
                    t.Stop();
                    w.PrepareForCapture();
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    t2.Tick += (s3, e3) =>
                    {
                        t2.Stop();
                        try
                        {
                            string p = Path.Combine(outDir, "real_preview.png");
                            Capture(w, p);
                            Console.WriteLine("截图: " + p);

                            // 打印真实树里的内容，证明是真实数据
                            w.DumpTree();
                        }
                        catch (Exception ex) { Console.WriteLine("ERR " + ex); }
                        app.Shutdown();
                    };
                    t2.Start();
                };
                t.Start();
            };

            app.Run(w);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        static void Capture(Window w, string path)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            RECT r;
            GetWindowRect(hwnd, out r);
            int cw = Math.Max(1, r.Right - r.Left);
            int ch = Math.Max(1, r.Bottom - r.Top);

            using (var bmp = new System.Drawing.Bitmap(cw, ch))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0,
                        new System.Drawing.Size(cw, ch),
                        System.Drawing.CopyPixelOperation.SourceCopy);
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
