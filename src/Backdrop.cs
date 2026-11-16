using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LiquidUnzip
{
    /// <summary>
    /// 桌面背板策略。
    ///
    /// 性能实测结论（见 docs/PERF.md）：
    ///   在 AllowsTransparency=true 的窗口里每帧贴一张图，UI 停顿从 19ms 涨到 29ms，
    ///   加不加模糊差别很小 —— 瓶颈是 **WPF 软件合成**，不是 BlurEffect。
    ///
    /// 因此默认走 **系统 Acrylic**（DWM 硬件合成，CPU 开销接近 0），
    /// 它本身就是"透出桌面 + 均匀磨砂"。
    /// 只有用户主动开启"采集模式"时才启用逐帧抓屏（能捕捉背后的窗口变化，
    /// 但只能走软件合成，代价是拖动不够跟手）。
    /// </summary>
    public class Backdrop : IDisposable
    {
        const uint WDA_NONE = 0x0;
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        /// <summary>采集目标的最大边长。越小越流畅。</summary>
        public static int MaxDim = 200;

        /// <summary>采集间隔（毫秒）。</summary>
        public static int IntervalMs = 140;

        [DllImport("user32.dll")]
        static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                  IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll")]
        static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                      IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc,
                                      int rop);

        [DllImport("gdi32.dll")]
        static extern int SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll")]
        static extern int SystemParametersInfo(int uAction, int uParam,
                                               System.Text.StringBuilder lpvParam, int fuWinIni);

        const int SRCCOPY = 0x00CC0020;
        const int HALFTONE = 4;
        const int SPI_GETDESKWALLPAPER = 0x0073;

        public bool CaptureAvailable { get; private set; }
        public bool UsingWallpaper { get; private set; }

        /// <summary>采集到的帧代数，UI 层据此判断要不要重绘。</summary>
        public long FrameId { get { return Interlocked.Read(ref _frameId); } }

        BitmapSource _frame;
        long _frameId;
        readonly object _lock = new object();
        Thread _thread;
        volatile bool _running;
        volatile bool _paused;
        IntPtr _hwnd;
        int _x, _y, _w, _h;

        public void Start(IntPtr hwnd, int x, int y, int w, int h)
        {
            _hwnd = hwnd;
            _x = x; _y = y; _w = w; _h = h;

            bool ok = false;
            try { ok = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
            catch { }
            CaptureAvailable = ok;

            if (!ok)
            {
                UsingWallpaper = true;
                var wp = LoadWallpaper(w, h);
                if (wp != null) { lock (_lock) _frame = wp; Interlocked.Increment(ref _frameId); }
                return;
            }

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "LiquidBackdrop", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        /// <summary>拖动 / 缩放窗口期间暂停采集，窗口静止后恢复。</summary>
        public void SetPaused(bool paused)
        {
            _paused = paused;
        }

        public void UpdateRect(int x, int y, int w, int h)
        {
            if (x == _x && y == _y && w == _w && h == _h) return;
            _x = x; _y = y; _w = w; _h = h;
        }

        void Loop()
        {
            while (_running)
            {
                try
                {
                    if (!_paused && _w > 0 && _h > 0)
                    {
                        var bmp = GrabScaled(_x, _y, _w, _h);
                        if (bmp != null)
                        {
                            lock (_lock) _frame = bmp;
                            Interlocked.Increment(ref _frameId);
                        }
                    }
                }
                catch { }
                Thread.Sleep(IntervalMs);
            }
        }

        /// <summary>抓屏并直接降采样到 MaxDim 以内 —— 这是流畅度的关键。</summary>
        BitmapSource GrabScaled(int x, int y, int w, int h)
        {
            // 目标尺寸
            double scale = Math.Min(1.0, (double)MaxDim / Math.Max(w, h));
            int dw = Math.Max(8, (int)(w * scale));
            int dh = Math.Max(8, (int)(h * scale));

            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return null;

                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return null;

                hBmp = CreateCompatibleBitmap(screenDc, dw, dh);
                if (hBmp == IntPtr.Zero) return null;

                old = SelectObject(memDc, hBmp);
                SetStretchBltMode(memDc, HALFTONE);

                bool ok;
                if (dw == w && dh == h)
                    ok = BitBlt(memDc, 0, 0, dw, dh, screenDc, x, y, SRCCOPY);
                else
                    ok = StretchBlt(memDc, 0, 0, dw, dh, screenDc, x, y, w, h, SRCCOPY);

                if (!ok) return null;

                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                if (old != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public BitmapSource Current
        {
            get { lock (_lock) return _frame; }
        }

        static BitmapSource LoadWallpaper(int w, int h)
        {
            try
            {
                var sb = new System.Text.StringBuilder(2048);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0) == 0) return null;
                string p = sb.ToString();
                if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) return null;

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(p);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = Math.Max(MaxDim, 200);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            _running = false;
            try { if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, WDA_NONE); } catch { }
        }
    }
}
