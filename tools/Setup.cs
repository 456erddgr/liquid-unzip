using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace LiquidUnzip.Setup
{
    /// <summary>
    /// Liquid Unzip 一键安装器。
    /// 三个程序集以嵌入资源的形式打包在本 exe 内，运行时解压到
    /// %LOCALAPPDATA%\LiquidUnzip，创建桌面/开始菜单快捷方式并启动。
    /// 卸载信息写入 HKCU，无需管理员权限。
    /// </summary>
    public class SetupWindow : Window
    {
        const string AppName = "Liquid Unzip";
        const string Version = "1.0.0";

        static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiquidUnzip");

        readonly string[] Payload = { "LiquidUnzip.exe", "7z.exe", "7z.dll" };

        TextBlock _status;
        ShapePath _arc;
        Border _card;
        TextBlock _pctText;
        bool _done;

        public SetupWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 420;
            Height = 300;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");

            BuildUI();

            Loaded += (s, e) =>
            {
                Glass.EnableAcrylic(this, 0x99101014, true);
                AnimateIn();
                StartInstall();
            };
        }

        void BuildUI()
        {
            var outer = new Grid();
            _card = new Border
            {
                CornerRadius = new CornerRadius(20),
                Margin = new Thickness(16),
                Background = Glass.GlassFill(0.10, 0.04),
                BorderBrush = Glass.SpecularEdge(0.45, 0.10, 0.30),
                BorderThickness = new Thickness(1),
                Effect = Glass.SoftShadow(26, 0.5, 6)
            };
            outer.Children.Add(_card);

            var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
            _card.Child = stack;

            // 图标
            var iconHost = new Grid { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Left };
            iconHost.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(240, 10, 132, 255)) });
            var geo = new PathGeometry();
            var f1 = new PathFigure { StartPoint = new Point(0.5, 0.08) };
            f1.Segments.Add(new LineSegment(new Point(0.5, 0.62), true));
            geo.Figures.Add(f1);
            var f2 = new PathFigure { StartPoint = new Point(0.20, 0.40) };
            f2.Segments.Add(new LineSegment(new Point(0.5, 0.70), true));
            f2.Segments.Add(new LineSegment(new Point(0.80, 0.40), true));
            geo.Figures.Add(f2);
            iconHost.Children.Add(new ShapePath
            {
                Data = geo,
                Stroke = Brushes.White,
                StrokeThickness = 2.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = 22, Height = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(iconHost);

            stack.Children.Add(new TextBlock
            {
                Text = AppName,
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                Margin = new Thickness(0, 14, 0, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "液态玻璃 · 多进程并行解压",
                FontSize = 12,
                Foreground = new SolidColorBrush(Theme.TextSecondary),
                Margin = new Thickness(0, 4, 0, 0)
            });

            _status = new TextBlock
            {
                Text = "准备安装…",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Theme.TextTertiary),
                Margin = new Thickness(0, 20, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(_status);

            // 进度环（小尺寸）
            var ringHost = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
            ringHost.Children.Add(new Ellipse
            {
                Stroke = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                StrokeThickness = 4
            });
            _arc = new ShapePath
            {
                Stroke = Glass.RingGradient(Theme.Sky, Theme.Cyan),
                StrokeThickness = 4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            ringHost.Children.Add(_arc);
            _pctText = new TextBlock
            {
                Text = "0%",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Theme.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ringHost.Children.Add(_pctText);
            stack.Children.Add(ringHost);

            // 关闭按钮（右上）
            var close = new Button
            {
                Width = 22, Height = 22,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0),
                Content = new TextBlock
                {
                    Text = "✕", FontSize = 11,
                    Foreground = new SolidColorBrush(Theme.TextTertiary)
                }
            };
            close.Click += (s, e) => { if (_done) Close(); };
            var overlay = new Grid();
            Grid.SetRowSpan(overlay, 1);
            inner = overlay;
            overlay.Children.Add(close);
            outer.Children.Add(overlay);

            // 拖动
            _card.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
            };

            Content = outer;
            _statusBar = _status;
        }

        Grid inner;
        TextBlock _statusBar;

        void AnimateIn()
        {
            _card.RenderTransformOrigin = new Point(0.5, 0.5);
            _card.RenderTransform = new ScaleTransform(0.97, 0.97);
            var st = (ScaleTransform)_card.RenderTransform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        }

        void SetProgress(double pct)
        {
            _pctText.Text = ((int)(pct * 100)) + "%";
            double angle = Math.Max(0.001, pct * 360.0);
            double r = 17, cx = 20, cy = 20;
            double rad = (angle - 90) * Math.PI / 180.0;
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(cx, cy - r) };
            fig.Segments.Add(new ArcSegment
            {
                Point = new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad)),
                Size = new Size(r, r),
                IsLargeArc = angle > 180,
                SweepDirection = SweepDirection.Clockwise
            });
            geo.Figures.Add(fig);
            _arc.Data = geo;
        }

        async void StartInstall()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(260);

                _statusBar.Text = "创建安装目录…";
                SetProgress(0.12);
                Directory.CreateDirectory(InstallDir);

                int i = 0;
                foreach (var name in Payload)
                {
                    i++;
                    _statusBar.Text = "写入 " + name + " …";
                    string dst = Path.Combine(InstallDir, name);
                    ExtractResource("Payload." + name, dst);
                    SetProgress(0.12 + 0.62 * i / Payload.Length);
                    await System.Threading.Tasks.Task.Delay(120);
                }

                _statusBar.Text = "创建快捷方式…";
                SetProgress(0.82);
                CreateShortcuts();

                _statusBar.Text = "注册卸载信息…";
                SetProgress(0.92);
                RegisterUninstall();

                _statusBar.Text = "安装完成，正在启动…";
                SetProgress(1.0);
                _done = true;
                await System.Threading.Tasks.Task.Delay(420);

                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(InstallDir, "LiquidUnzip.exe"),
                    WorkingDirectory = InstallDir,
                    UseShellExecute = true
                });

                Close();
            }
            catch (Exception ex)
            {
                _statusBar.Text = "安装失败：" + ex.Message;
                _statusBar.Foreground = new SolidColorBrush(Theme.Red);
                _done = true;
            }
        }

        static void ExtractResource(string resName, string dst)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(resName))
            {
                if (s == null) throw new Exception("缺少资源 " + resName);
                using (var fs = File.Create(dst)) s.CopyTo(fs);
            }
        }

        static void CreateShortcuts()
        {
            string exe = Path.Combine(InstallDir, "LiquidUnzip.exe");

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            MakeLnk(Path.Combine(desktop, AppName + ".lnk"), exe);

            string start = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string folder = Path.Combine(start, AppName);
            Directory.CreateDirectory(folder);
            MakeLnk(Path.Combine(folder, AppName + ".lnk"), exe);
        }

        /// <summary>用 WScript.Shell 创建 .lnk（避免依赖 COM interop 程序集）。</summary>
        static void MakeLnk(string lnkPath, string target)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                dynamic sh = Activator.CreateInstance(t);
                dynamic sc = sh.CreateShortcut(lnkPath);
                sc.TargetPath = target;
                sc.WorkingDirectory = InstallDir;
                sc.IconLocation = target + ",0";
                sc.Description = "液态玻璃一键解压工具";
                sc.Save();
            }
            catch { }
        }

        static void RegisterUninstall()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LiquidUnzip"))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", "LiquidUnzip");
                    k.SetValue("DisplayIcon", Path.Combine(InstallDir, "LiquidUnzip.exe"));
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString",
                        "\"" + Path.Combine(InstallDir, "LiquidUnzip.exe") + "\" --uninstall");
                    k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }

                // 右键菜单：用 Liquid Unzip 解压
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\*\shell\LiquidUnzip"))
                {
                    if (k != null)
                    {
                        k.SetValue("", "用 Liquid Unzip 解压");
                        k.SetValue("Icon", Path.Combine(InstallDir, "LiquidUnzip.exe"));
                    }
                }
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\*\shell\LiquidUnzip\command"))
                {
                    if (k != null)
                        k.SetValue("", "\"" + Path.Combine(InstallDir, "LiquidUnzip.exe") + "\" \"%1\"");
                }
            }
            catch { }
        }
    }

    public static class SetupProgram
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var w = new SetupWindow();
            app.MainWindow = w;
            app.Run(w);
        }
    }
}
