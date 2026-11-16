using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LiquidUnzip
{
    /// <summary>
    /// 逐项剥离开销，定位"拖动卡顿"的真实来源。
    /// 每一轮都在同样条件下改变窗口尺寸，测 UI 停顿分布。
    /// </summary>
    public static class TestPerf2
    {
        static Application _app;

        [STAThread]
        public static int Main(string[] args)
        {
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            Console.WriteLine("CPU 核心: " + Environment.ProcessorCount);
            Console.WriteLine();

            // 用一个隐藏的宿主窗口承载消息循环，逐个跑场景
            var host = new Window
            {
                Width = 1, Height = 1,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -2000, Top = -2000
            };
            host.Show();

            var scenarios = new List<Tuple<string, Action<Grid>, bool, int>>();
            scenarios.Add(Tuple.Create<string, Action<Grid>, bool, int>("A 空窗口(透明)", null, false, 0));
            scenarios.Add(Tuple.Create<string, Action<Grid>, bool, int>("B 背板Image(无模糊)", BuildLayers(0), false, 0));
            scenarios.Add(Tuple.Create<string, Action<Grid>, bool, int>("C Blur radius20", BuildLayers(20), false, 0));
            scenarios.Add(Tuple.Create<string, Action<Grid>, bool, int>("D Blur + 每帧换图", BuildLayers(20), true, 0));
            scenarios.Add(Tuple.Create<string, Action<Grid>, bool, int>("E Blur + 尺寸时冻结", BuildLayers(20), true, 1));

            int idx = 0;
            Action next = null;
            next = () =>
            {
                if (idx >= scenarios.Count)
                {
                    Console.WriteLine();
                    Console.WriteLine("判据：p95 < 25ms 视为流畅");
                    host.Close();
                    _app.Shutdown();
                    return;
                }
                var sc = scenarios[idx++];
                Bench(sc.Item1, sc.Item2, sc.Item3, sc.Item4, next);
            };
            next();

            _app.Run();
            return 0;
        }

        static Action<Grid> BuildLayers(double blurRadius)
        {
            return host =>
            {
                var img = new Image { Stretch = Stretch.UniformToFill };
                if (blurRadius > 0)
                    img.Effect = new BlurEffect { Radius = blurRadius, RenderingBias = RenderingBias.Performance };

                // 造一张 200px 的小图当背板
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    var b = new LinearGradientBrush(Colors.DarkSlateBlue, Colors.Sienna, 30);
                    dc.DrawRectangle(b, null, new Rect(0, 0, 200, 150));
                }
                var rtb = new RenderTargetBitmap(200, 150, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                img.Source = rtb;
                host.Children.Add(img);

                var tint = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(180, 10, 12, 18))
                };
                host.Children.Add(tint);
            };
        }

        static void Bench(string name, Action<Grid> build, bool swapImage, int freezeMode, Action done)
        {
            var w = new Window
            {
                Width = 540,
                Height = 580,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 120,
                Top = 120
            };

            var host = new Grid { ClipToBounds = true };
            w.Content = host;
            if (build != null) build(host);

            bool frozen = false;
            var stops = new List<double>();
            int frames = 0;
            double phase = 0;
            double baseW = 540, baseH = 580;

            var sw = Stopwatch.StartNew();
            var sizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            var swapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            var end = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };

            sizeTimer.Tick += (s, e) =>
            {
                if (freezeMode == 1)
                {
                    // 模拟"尺寸变化时冻结"：把模糊半径压到 0
                    foreach (var c in host.Children)
                    {
                        var im = c as Image;
                        if (im != null && im.Effect is BlurEffect)
                            ((BlurEffect)im.Effect).Radius = 0;
                    }
                    frozen = true;
                }

                var t0 = Stopwatch.StartNew();
                phase += 0.35;
                w.Width = baseW + Math.Sin(phase) * 20;
                w.Height = baseH + Math.Cos(phase) * 14;
                w.UpdateLayout();
                t0.Stop();
                stops.Add(t0.Elapsed.TotalMilliseconds);
                frames++;
            };

            if (swapImage)
            {
                swapTimer.Tick += (s, e) =>
                {
                    foreach (var c in host.Children)
                    {
                        var im = c as Image;
                        if (im == null) continue;
                        var dv = new DrawingVisual();
                        using (var dc = dv.RenderOpen())
                        {
                            var b = new LinearGradientBrush(Colors.DarkSlateBlue, Colors.Peru, 30 + frames % 60);
                            dc.DrawRectangle(b, null, new Rect(0, 0, 200, 150));
                        }
                        var rtb = new RenderTargetBitmap(200, 150, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(dv);
                        rtb.Freeze();
                        im.Source = rtb;
                    }
                };
            }

            end.Tick += (s, e) =>
            {
                sizeTimer.Stop();
                swapTimer.Stop();
                end.Stop();

                stops.Sort();
                if (stops.Count == 0)
                {
                    Console.WriteLine(name + ": 无样本");
                    w.Close();
                    done();
                    return;
                }

                double p50 = stops[stops.Count / 2];
                double p95 = stops[Math.Min(stops.Count - 1, (int)(stops.Count * 0.95))];
                double max = stops[stops.Count - 1];
                double total = sw.Elapsed.TotalMilliseconds;

                Console.WriteLine(string.Format(
                    "{0,-28} 帧{1,3}  p50={2,6:0.0}  p95={3,6:0.0}  max={4,6:0.0}  均帧隔={5,5:0.0}ms  {6}",
                    name, frames, p50, p95, max, total / Math.Max(1, frames),
                    p95 < 25 ? "OK" : "SLOW"));

                w.Close();
                done();
            };

            w.Loaded += (s, e) =>
            {
                sizeTimer.Start();
                if (swapImage) swapTimer.Start();
                end.Start();
            };
            w.Show();
            // 立即把主窗口尺寸设好再开始计时，避免加载抖动计入
            w.UpdateLayout();
        }
    }
}
