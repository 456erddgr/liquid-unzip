using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiquidUnzip
{
    /// <summary>
    /// 用最小窗口逐项测"拖动跟手度"，定位真实卡顿来源。
    /// 每轮拖动 2.5 秒，记录相邻 tick 的实际间隔（理想 16ms）。
    /// </summary>
    public static class TestDrag2
    {
        static Application _app;

        [STAThread]
        public static int Main(string[] args)
        {
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Console.WriteLine("CPU 核心: " + Environment.ProcessorCount);
            Console.WriteLine("判据: p95 < 34ms 视为跟手");
            Console.WriteLine();

            var host = new Window { Width = 1, Height = 1, ShowInTaskbar = false, Left = -3000, Top = -3000 };
            host.Show();

            var cases = new List<Tuple<string, Action<Window>>>();

            cases.Add(Tuple.Create<string, Action<Window>>("1 空窗口(不透明)", w => { }));

            cases.Add(Tuple.Create<string, Action<Window>>("2 Acrylic 玻璃", w =>
            {
                Glass.EnableDesktopGlass(w, 0x8C0E1018);
            }));

            cases.Add(Tuple.Create<string, Action<Window>>("3 Acrylic + 半透明子层", w =>
            {
                Glass.EnableDesktopGlass(w, 0x8C0E1018);
                var g = new Grid();
                g.Children.Add(new Border
                {
                    Background = new LinearGradientBrush(
                        Color.FromArgb(96, 10, 12, 18), Color.FromArgb(140, 6, 8, 14), 90),
                    CornerRadius = new CornerRadius(20)
                });
                w.Content = g;
            }));

            cases.Add(Tuple.Create<string, Action<Window>>("4 Acrylic + 5层渐变", w =>
            {
                Glass.EnableDesktopGlass(w, 0x8C0E1018);
                var g = new Grid();
                var lg = new LiquidGlassSurface();
                lg.ApplyStrength(0.62);
                g.Children.Add(lg.Host);
                w.Content = g;
            }));

            int idx = 0;
            Action next = null;
            next = () =>
            {
                if (idx >= cases.Count) { host.Close(); _app.Shutdown(); return; }
                var c = cases[idx++];
                RunCase(c.Item1, c.Item2, next);
            };
            next();

            _app.Run();
            return 0;
        }

        static void RunCase(string name, Action<Window> setup, Action done)
        {
            var w = new Window
            {
                Width = 540,
                Height = 580,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                Background = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 200,
                Top = 160
            };
            setup(w);

            var gaps = new List<double>();
            var last = Stopwatch.StartNew();
            double phase = 0;
            double baseL = w.Left, baseT = w.Top;

            var move = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            move.Tick += (s, e) =>
            {
                gaps.Add(last.Elapsed.TotalMilliseconds);
                last = Stopwatch.StartNew();
                phase += 0.22;
                w.Left = baseL + Math.Sin(phase) * 120;
                w.Top = baseT + Math.Cos(phase) * 60;
                w.UpdateLayout();
            };

            var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
            stop.Tick += (s, e) =>
            {
                stop.Stop();
                move.Stop();
                if (gaps.Count > 3) gaps.RemoveRange(0, 2);
                gaps.Sort();
                if (gaps.Count == 0) { Console.WriteLine(name + ": 无样本"); w.Close(); done(); return; }

                double p50 = gaps[gaps.Count / 2];
                double p95 = gaps[Math.Min(gaps.Count - 1, (int)(gaps.Count * 0.95))];
                double max = gaps[gaps.Count - 1];
                double avg = 0;
                foreach (var g in gaps) avg += g;
                avg /= gaps.Count;

                Console.WriteLine(string.Format("{0,-26} n={1,3}  avg={2,5:0.0}  p50={3,5:0.0}  p95={4,5:0.0}  max={5,5:0.0}  {6}",
                    name, gaps.Count, avg, p50, p95, max, p95 < 34 ? "OK" : "SLOW"));

                w.Close();
                done();
            };

            w.Loaded += (s, e) => { move.Start(); stop.Start(); };
            w.Show();
        }
    }
}
