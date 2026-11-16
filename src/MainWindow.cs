using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using Path = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace LiquidUnzip
{
    public class HistoryItem
    {
        public string ArchivePath { get; set; }
        public string DestPath { get; set; }
        public DateTime When { get; set; }
        public long Bytes { get; set; }
        public int Seconds { get; set; }
    }

    public class MainWindow : Window
    {
        // ---- 状态 ----
        readonly string _sevenZip;
        Extractor _ext;
        CancellationTokenSource _cts;
        bool _busy;
        string _pickedArchive;
        List<HistoryItem> _history = new List<HistoryItem>();

        // ---- 视觉元素 ----
        Border _rootCard;
        Border _dropZone;
        TextBlock _titleText;
        TextBlock _subText;
        TextBlock _statLine;
        TextBlock _pctText;
        TextBlock _speedText;
        TextBlock _fileText;
        Grid _progressHost;
        TextBlock _ringPct;
        ListBox _histList;
        Border _histHost;
        FrameworkElement _previewHost;
        TreeView _tree;
        ObservableCollection<ArcNode> _treeRoot;
        List<Extractor.Entry> _entries;
        TextBlock _previewSummary;
        Button _extractSelBtn;
        int _userConcurrency = 0;      // 0 = 自动
        double _glassStrength = 0.62;
        TextBlock _concLabel;
        TextBlock _glassLabel;

        /// <summary>并行数与玻璃强度调节行。</summary>
        FrameworkElement BuildSettingsRow()
        {
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左：并行数
            var l = new StackPanel();
            _concLabel = new TextBlock
            {
                Text = "并行  自动",
                FontSize = 10,
                Foreground = new SolidColorBrush(Theme.TextTertiary)
            };
            l.Children.Add(_concLabel);
            var s1 = new Slider
            {
                Minimum = 0, Maximum = 16, Value = 0,
                TickFrequency = 1, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 3, 0, 0),
                Height = 16,
                Focusable = false,
                Style = BuildSliderStyle(),
                ToolTip = "0 = 自动（CPU 核心数 / 2，限制 4–16）\nCPU 较弱时调低可减少卡顿"
            };
            s1.ValueChanged += (s, e) =>
            {
                int v = (int)s1.Value;
                _userConcurrency = v;
                _concLabel.Text = v == 0
                    ? "并行  自动（" + AutoConcurrency() + "）"
                    : "并行  " + v + " 路";
            };
            _concLabel.Text = "并行  自动（" + AutoConcurrency() + "）";
            l.Children.Add(s1);
            g.Children.Add(l);

            // 右：玻璃强度
            var r = new StackPanel();
            _glassLabel = new TextBlock
            {
                Text = "玻璃  62%",
                FontSize = 10,
                Foreground = new SolidColorBrush(Theme.TextTertiary)
            };
            r.Children.Add(_glassLabel);
            var s2 = new Slider
            {
                Minimum = 0, Maximum = 1, Value = _glassStrength,
                Margin = new Thickness(0, 3, 0, 0),
                Height = 16,
                Focusable = false,
                Style = BuildSliderStyle(),
                ToolTip = "调整磨砂强度：越低越透，越高越浑"
            };
            s2.ValueChanged += (s, e) =>
            {
                _glassStrength = s2.Value;
                _glassLabel.Text = "玻璃  " + (int)(_glassStrength * 100) + "%";
                if (_glass != null) _glass.ApplyStrength(_glassStrength);
            };
            r.Children.Add(s2);
            Grid.SetColumn(r, 2);
            g.Children.Add(r);

            // 第二行：两个开关
            var toggles = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };
            toggles.Children.Add(BuildLiveToggle());
            toggles.Children.Add(BuildPerfToggle());

            var wrap = new StackPanel();
            wrap.Children.Add(g);
            wrap.Children.Add(toggles);
            return wrap;
        }

        /// <summary>底栏的"实时背景"开关：开启后能捕捉背后窗口的变化，但更吃 CPU。</summary>
        FrameworkElement BuildLiveToggle()
        {
            var cb = new CheckBox
            {
                Content = "实时背景",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 210, 216, 230)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                Focusable = false,
                ToolTip = "关闭时用系统 Acrylic（最流畅）\n开启后逐帧采集桌面，能看到背后窗口的变化，但更吃 CPU"
            };
            cb.Checked += (s, e) => SetLiveBackdrop(true);
            cb.Unchecked += (s, e) => SetLiveBackdrop(false);
            return cb;
        }

        /// <summary>切换逐帧采集（能捕捉背后窗口变化，但更吃 CPU）。</summary>
        void SetLiveBackdrop(bool on)
        {
            try
            {
                EnableLiveBackdrop(on);
                if (_glass != null) _glass.SetLiveMode(on);
            }
            catch { }
        }

        /// <summary>底栏的"性能模式"开关：关掉玻璃改纯色，适合核显/远程桌面。</summary>
        FrameworkElement BuildPerfToggle()
        {
            var cb = new CheckBox
            {
                Content = "性能模式",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 210, 216, 230)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 8, 0, 0),
                Focusable = false,
                ToolTip = "关掉毛玻璃改用纯色背景\n核显、远程桌面、老机器上更顺滑"
            };
            cb.Checked += (s, e) => SetPerfMode(true);
            cb.Unchecked += (s, e) => SetPerfMode(false);
            return cb;
        }

        bool _perfMode;

        /// <summary>性能模式：彻底关掉 DWM Acrylic 与所有渐变叠层。</summary>
        void SetPerfMode(bool on)
        {
            _perfMode = on;
            try
            {
                if (on)
                {
                    // 取消玻璃：换回不透明深色
                    Glass.DisableGlass(this);
                    if (_glass != null) _glass.Host.Visibility = Visibility.Collapsed;
                    Background = new SolidColorBrush(Color.FromRgb(20, 22, 30));
                    _rootCard.Background = Brushes.Transparent;
                    _rootCard.BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
                }
                else
                {
                    Glass.EnableDesktopGlass(this, 0x40101820);
                    if (_glass != null) _glass.Host.Visibility = Visibility.Visible;
                    Background = Brushes.Transparent;
                    _rootCard.Background = new LinearGradientBrush(
                        Color.FromArgb(14, 255, 255, 255),
                        Color.FromArgb(4, 255, 255, 255), 90);
                    _rootCard.BorderBrush = Glass.SpecularEdge(0.26, 0.04, 0.16);
                }
            }
            catch { }
        }

        /// <summary>预览面板：归档文件树 + 全选/反选 + 单独解压选中项。</summary>
        FrameworkElement BuildPreviewPanel()
        {
            var wrap = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new LinearGradientBrush(
                    Color.FromArgb(52, 10, 12, 18),
                    Color.FromArgb(38, 6, 8, 14), 90),
                BorderBrush = new SolidColorBrush(Color.FromArgb(44, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0)
            };

            var g = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 标题行
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 1 树
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 底部操作
            wrap.Child = g;

            // ---- 标题行 ----
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _previewSummary = new TextBlock
            {
                Text = "压缩包内容",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 214, 218, 228)),
                VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(_previewSummary);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            var allBtn = BuildMiniButton("全选");
            allBtn.Click += (s, e) => SetAllChecked(true);
            var noneBtn = BuildMiniButton("清空");
            noneBtn.Click += (s, e) => SetAllChecked(false);
            btnRow.Children.Add(allBtn);
            btnRow.Children.Add(noneBtn);
            Grid.SetColumn(btnRow, 1);
            head.Children.Add(btnRow);

            g.Children.Add(head);

            // ---- 文件树 ----
            _tree = new TreeView
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 6, 0, 6),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                ItemContainerStyle = BuildTreeItemStyle()
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_tree, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);

            var tpl = new HierarchicalDataTemplate(typeof(ArcNode));
            tpl.ItemsSource = new Binding("Children");
            tpl.VisualTree = BuildTreeRow();
            _tree.ItemTemplate = tpl;

            Grid.SetRow(_tree, 1);
            g.Children.Add(_tree);

            // ---- 底栏 ----
            var foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var outRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var outIcon = new TextBlock
            {
                Text = "输出", FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(190, 200, 206, 220)),
                VerticalAlignment = VerticalAlignment.Center
            };
            outRow.Children.Add(outIcon);
            _outDirText = new TextBlock
            {
                Text = " 压缩包同级目录",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(230, 220, 226, 238)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260
            };
            outRow.Children.Add(_outDirText);
            var pickOut = BuildMiniButton("选择…");
            pickOut.Click += (s, e) => ChooseOutputDir();
            outRow.Children.Add(pickOut);
            foot.Children.Add(outRow);

            // 提示：勾选后只解压选中的部分
            var hint = new TextBlock
            {
                Text = "可勾选单项",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(140, 200, 206, 222)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0)
            };
            Grid.SetColumn(hint, 1);
            foot.Children.Add(hint);
            _selHint = hint;

            _extractSelBtn = BuildPrimaryButton("解压选中");
            _extractSelBtn.FontSize = 11;
            _extractSelBtn.Padding = new Thickness(12, 5, 12, 5);
            _extractSelBtn.IsEnabled = false;
            _extractSelBtn.Click += (s, e) => ExtractSelected();
            Grid.SetColumn(_extractSelBtn, 2);
            foot.Children.Add(_extractSelBtn);

            Grid.SetRow(foot, 2);
            g.Children.Add(foot);

            return wrap;
        }

        TextBlock _outDirText;
        FrameworkElement _mainDestWrap;
        TextBlock _selHint;

        void SetAllChecked(bool on)
        {
            if (_treeRoot == null) return;
            foreach (var n in _treeRoot) n.IsChecked = on;
            UpdateSelButton();
        }

        void UpdateSelButton()
        {
            if (_treeRoot == null || _extractSelBtn == null) return;
            var sel = new List<string>();
            ArcNode.CollectChecked(_treeRoot, sel);
            _extractSelBtn.IsEnabled = sel.Count > 0 && !_busy;
            _extractSelBtn.Content = sel.Count > 0 ? "解压选中 " + sel.Count : "解压选中";
        }

        Button BuildMiniButton(string text)
        {
            var b = new Button
            {
                Content = text,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(235, 226, 232, 244)),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255))
            };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            b.Template = tpl;
            return b;
        }

        static Style BuildTreeItemStyle()
        {
            var st = new Style(typeof(TreeViewItem));
            st.Setters.Add(new Setter(ForegroundProperty,
                new SolidColorBrush(Color.FromArgb(245, 236, 240, 250))));
            st.Setters.Add(new Setter(FontSizeProperty, 10.5));
            st.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
            st.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
                new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
            return st;
        }

        /// <summary>树的一行：勾选框 + 类型图标 + 名字 + 大小。</summary>
        static FrameworkElementFactory BuildTreeRow()
        {
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var cb = new FrameworkElementFactory(typeof(CheckBox));
            cb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cb.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            cb.SetValue(FrameworkElement.FocusableProperty, false);
            cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding("IsChecked") { Mode = BindingMode.TwoWay });
            row.AppendChild(cb);

            // 类型图标：符号 + 按类型着色
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetBinding(TextBlock.TextProperty, new Binding("Glyph"));
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            glyph.SetValue(TextBlock.FontSizeProperty, 9.5);
            glyph.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            glyph.SetValue(TextBlock.FontFamilyProperty,
                new FontFamily("Segoe UI Symbol, Segoe UI, Microsoft YaHei UI"));
            var colorBinding = new Binding("GlyphColor");
            colorBinding.Converter = new HexToBrushConverter();
            glyph.SetBinding(TextBlock.ForegroundProperty, colorBinding);
            row.AppendChild(glyph);

            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            name.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            row.AppendChild(name);

            var size = new FrameworkElementFactory(typeof(TextBlock));
            size.SetBinding(TextBlock.TextProperty, new Binding("SizeText"));
            size.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            size.SetValue(TextBlock.FontSizeProperty, 9.0);
            size.SetValue(TextBlock.ForegroundProperty,
                new SolidColorBrush(Color.FromArgb(150, 200, 206, 222)));
            row.AppendChild(size);

            return row;
        }

        /// <summary>解压用户勾选的文件。</summary>
        void ExtractSelected()
        {
            if (_treeRoot == null || _pickedArchive == null || _ext == null || _busy) return;

            var sel = new List<string>();
            ArcNode.CollectChecked(_treeRoot, sel);
            if (sel.Count == 0) return;

            StartExtractSelected(sel);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HTCAPTION = 0x2;

        /// <summary>启动系统原生窗口拖动（顺滑，支持贴边）。</summary>
        void StartNativeDrag()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                ReleaseCapture();
                SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>放大成横屏（预览树能铺开）/ 还原成竖版。</summary>
        public void ToggleWide()
        {
            _wide = !_wide;
            var wa = SystemParameters.WorkArea;
            // WorkArea 是 DIP 单位，窗口尺寸也是 DIP，直接比较即可
            if (_wide)
            {
                double w = Math.Min(980, wa.Width * 0.78);
                double h = Math.Min(600, wa.Height * 0.80);
                AnimateSize(w, h);
            }
            else
            {
                AnimateSize(540, Math.Min(580, wa.Height * 0.80));
            }
        }

        bool _wide;
        Button _sizeBtn;

        void AnimateSize(double w, double h)
        {
            double w0 = ActualWidth, h0 = ActualHeight;
            // 保持窗口中心不动
            double cx = Left + w0 / 2, cy = Top + h0 / 2;
            double tx = cx - w / 2, ty = cy - h / 2;

            // 夹到工作区内，别让窗口跑到屏幕外
            var wa = SystemParameters.WorkArea;
            tx = Math.Max(wa.Left, Math.Min(tx, wa.Right - w));
            ty = Math.Max(wa.Top, Math.Min(ty, wa.Bottom - h));

            var dur = TimeSpan.FromMilliseconds(200);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var wA = new DoubleAnimation(w0, w, dur) { EasingFunction = ease };
            var hA = new DoubleAnimation(h0, h, dur) { EasingFunction = ease };
            var xA = new DoubleAnimation(Left, tx, dur) { EasingFunction = ease };
            var yA = new DoubleAnimation(Top, ty, dur) { EasingFunction = ease };

            wA.Completed += (s, e) =>
            {
                BeginAnimation(WidthProperty, null);
                BeginAnimation(HeightProperty, null);
                BeginAnimation(LeftProperty, null);
                BeginAnimation(TopProperty, null);
                Width = w; Height = h; Left = tx; Top = ty;
                SyncBackdropRect();
            };

            BeginAnimation(WidthProperty, wA);
            BeginAnimation(HeightProperty, hA);
            BeginAnimation(LeftProperty, xA);
            BeginAnimation(TopProperty, yA);
        }

        /// <summary>放大轮廓图标。</summary>
        static FrameworkElement ExpandGlyph(double size, Brush brush)
        {
            var geo = new PathGeometry();
            // 右上角双箭头式展开标记
            var a = new PathFigure { StartPoint = new Point(0.30, 0.70) };
            a.Segments.Add(new LineSegment(new Point(0.74, 0.26), true));
            geo.Figures.Add(a);
            var b = new PathFigure { StartPoint = new Point(0.48, 0.24) };
            b.Segments.Add(new LineSegment(new Point(0.76, 0.24), true));
            b.Segments.Add(new LineSegment(new Point(0.76, 0.52), true));
            geo.Figures.Add(b);

            return new ShapePath
            {
                Data = geo,
                Stroke = brush,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        int AutoConcurrency()
        {
            return Math.Max(4, Math.Min(16, Environment.ProcessorCount / 2));
        }

        /// <summary>
        /// 玻璃风格滑杆。
        /// 注意：不用自定义 ControlTemplate —— Slider 模板需要 PART_Track 且 Track 部件
        /// 无法用 FrameworkElementFactory 装配（Track 不支持 IAddChild，其部件属性是只读的）。
        /// 自定义模板的代价是滑杆直接拖不动，所以这里保留系统模板，只用样式微调配色。
        /// </summary>
        static Style BuildSliderStyle()
        {
            var st = new Style(typeof(Slider));
            st.Setters.Add(new Setter(FrameworkElement.HeightProperty, 20.0));
            st.Setters.Add(new Setter(FrameworkElement.FocusableProperty, false));
            st.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0)));
            return st;
        }

        int EffectiveConcurrency()
        {
            return _userConcurrency > 0 ? _userConcurrency : AutoConcurrency();
        }
        Button _mainBtn;
        Button _destBtn;
        TextBlock _destText;
        Canvas _spark;
        readonly List<double> _speedSamples = new List<double>();

        // 颜色
        static readonly Color Accent = Theme.Sky;
        static readonly Color Accent2 = Theme.Cyan;

        public MainWindow()
        {
            _sevenZip = Extractor.LocateSevenZip();

            WindowStyle = WindowStyle.None;
            // 用系统 Acrylic：DWM 硬件合成，透出桌面且几乎不耗 CPU。
            // （实测：AllowsTransparency=true 的软件合成会让 UI 停顿翻倍，见 docs/PERF.md）
            AllowsTransparency = false;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 540;
            Height = 580;
            MinWidth = 480;
            MinHeight = 460;
            ShowInTaskbar = true;
            Title = "Liquid Unzip";
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");

            LoadHistory();
            BuildUI();

            Loaded += (s, e) =>
            {
                // 系统 Acrylic：硬件合成，桌面真实透出，CPU 开销接近 0
                Glass.EnableDesktopGlass(this, 0x40101820);
                StartGlassBackdrop();
                AnimateIn();
            };

            SizeChanged += (s, e) =>
            {
                SyncBackdropRect();
                // 尺寸变化期间不重绘模糊（动画缩放也会走到这里）
                if (_glass != null) _glass.Freeze(true);
                if (_resizeIdle == null)
                {
                    _resizeIdle = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(160)
                    };
                    _resizeIdle.Tick += (s2, e2) =>
                    {
                        _resizeIdle.Stop();
                        if (_glass != null) _glass.Freeze(false);
                    };
                }
                _resizeIdle.Stop();
                _resizeIdle.Start();
            };
            LocationChanged += (s, e) => SyncBackdropRect();
        }

        System.Windows.Threading.DispatcherTimer _resizeIdle;

        /// <summary>
        /// 拖动/缩放窗口期间暂停背景采集 —— 这是"一动就卡半天"的关键修复。
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = (HwndSource)PresentationSource.FromVisual(this);
            if (src != null) src.AddHook(WndProc);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_ENTERSIZEMOVE = 0x0231;
            const int WM_EXITSIZEMOVE = 0x0232;

            if (msg == WM_ENTERSIZEMOVE)
            {
                if (_backdrop != null) _backdrop.SetPaused(true);
                if (_glassTimer != null) _glassTimer.Stop();
                if (_glass != null) _glass.Freeze(true);   // 关键：拖动时冻住模糊
            }
            else if (msg == WM_EXITSIZEMOVE)
            {
                if (_backdrop != null) _backdrop.SetPaused(false);
                if (_glass != null) _glass.Freeze(false);
                if (_glassTimer != null) _glassTimer.Start();
                SyncBackdropRect();
            }
            return IntPtr.Zero;
        }

        // ---- 液态玻璃背板 ----
        Backdrop _backdrop;
        LiquidGlassSurface _glass;
        System.Windows.Threading.DispatcherTimer _glassTimer;
        bool _liveBackdrop;      // 是否启用逐帧采集（默认关，性能优先）

        void StartGlassBackdrop()
        {
            // 默认不采集：系统 Acrylic 已经在硬件层做了"透出桌面"。
            // 只有开了实时背景才启动采集线程（需要注意性能代价）。
            _glassTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(140)
            };
            long lastId = -1;
            _glassTimer.Tick += (s2, e2) =>
            {
                if (_backdrop == null) return;
                long id = _backdrop.FrameId;
                if (id == lastId) return;
                lastId = id;
                var f = _backdrop.Current;
                if (f != null) _glass.SetFrame(f);
            };

            if (_liveBackdrop) EnableLiveBackdrop(true);
        }

        /// <summary>开/关逐帧背景采集。</summary>
        public void EnableLiveBackdrop(bool on)
        {
            _liveBackdrop = on;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (on)
            {
                // 采集需要窗口对截图隐身，且必须走透明合成
                if (_backdrop == null) _backdrop = new Backdrop();
                SyncBackdropRect();
                _backdrop.Start(hwnd, _rectX, _rectY, _rectW, _rectH);
                if (_glassTimer != null) _glassTimer.Start();
            }
            else
            {
                if (_glassTimer != null) _glassTimer.Stop();
                if (_backdrop != null) { _backdrop.Dispose(); _backdrop = null; }
            }
        }

        int _rectX, _rectY, _rectW, _rectH;

        void SyncBackdropRect()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                RECT r;
                if (GetWindowRect(hwnd, out r))
                {
                    _rectX = r.Left; _rectY = r.Top;
                    _rectW = r.Right - r.Left; _rectH = r.Bottom - r.Top;
                    if (_backdrop != null) _backdrop.UpdateRect(_rectX, _rectY, _rectW, _rectH);
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        protected override void OnClosed(EventArgs e)
        {
            if (_glassTimer != null) _glassTimer.Stop();
            if (_backdrop != null) _backdrop.Dispose();
            base.OnClosed(e);
        }

        /// <summary>截图专用：冻结背景帧并解除窗口隐身，让窗口能出现在截图中。</summary>
        public void PrepareForCapture()
        {
            try
            {
                if (_glassTimer != null) _glassTimer.Stop();
                if (_backdrop != null) _backdrop.Dispose();   // 解除 WDA_EXCLUDEFROMCAPTURE
            }
            catch { }
        }

        // =================================================================
        // UI 构建
        // =================================================================

        void BuildUI()
        {
            // 外层容器铺满窗口
            var outer = new Grid();

            // ---- 整片液态玻璃：底层就是桌面背板，铺满整个窗口 ----
            _glass = new LiquidGlassSurface();
            _glass.ApplyStrength(0.62);
            outer.Children.Add(_glass.Host);

            // 玻璃边缘：一圈内描边体现厚度
            var edge = new Border
            {
                CornerRadius = new CornerRadius(0),
                BorderBrush = Glass.SpecularEdge(0.34, 0.06, 0.20),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            outer.Children.Add(edge);

            // 内容卡片：只在玻璃上做极淡的分隔，本身不再单独模糊
            _rootCard = new Border
            {
                CornerRadius = new CornerRadius(20),
                Margin = new Thickness(1),
                Background = new LinearGradientBrush(
                    Color.FromArgb(10, 255, 255, 255),
                    Color.FromArgb(2, 255, 255, 255), 90),
                BorderBrush = Glass.SpecularEdge(0.22, 0.03, 0.13),
                BorderThickness = new Thickness(1)
            };
            outer.Children.Add(_rootCard);

            var content = new Grid { Margin = new Thickness(26, 22, 26, 18) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 0 标题栏
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2 主区
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 4 底栏
            _rootCard.Child = content;

            // ---- 标题栏 ----
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(head, 0);

            var icon = BuildIcon();
            head.Children.Add(icon);

            var titleStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _titleText = new TextBlock
            {
                Text = "Liquid Unzip",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                // 玻璃背景上的白字需要描边才在任何壁纸下都清晰
            };
            _subText = new TextBlock
            {
                Text = "拖入压缩包，一键解压",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(235, 228, 230, 238)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            titleStack.Children.Add(_titleText);
            titleStack.Children.Add(_subText);
            Grid.SetColumn(titleStack, 1);
            head.Children.Add(titleStack);

            var closeBtn = BuildCircleButton("", 12);
            closeBtn.Content = CrossGlyph(9, new SolidColorBrush(Theme.TextTertiary));
            closeBtn.Click += (s, e) => Close();
            closeBtn.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(closeBtn, 2);
            head.Children.Add(closeBtn);

            // 最大化/横屏按钮：双击标题栏或点这个按钮，在竖版与横屏之间切换
            _sizeBtn = BuildCircleButton("", 12);
            _sizeBtn.Content = ExpandGlyph(9, new SolidColorBrush(Theme.TextTertiary));
            _sizeBtn.ToolTip = "放大成横屏（再次点击还原）";
            _sizeBtn.Click += (s, e) => ToggleWide();
            _sizeBtn.VerticalAlignment = VerticalAlignment.Top;
            _sizeBtn.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetColumn(_sizeBtn, 2);
            // 与关闭按钮同列时用负边距错开
            _sizeBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _sizeBtn.Margin = new Thickness(0, 0, 26, 0);
            head.Children.Add(_sizeBtn);

            // 拖动：用 Win32 的 SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)
            // 这是系统原生拖动，比 DragMove() 顺滑得多，也支持拖到屏幕边缘触发贴边。
            head.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    ToggleWide();       // 双击标题栏 = 横屏/竖版切换
                    return;
                }
                if (e.ButtonState == MouseButtonState.Pressed) StartNativeDrag();
            };

            content.Children.Add(head);

            // ---- 主区：拖放区 + 进度 ----
            var body = new Grid();
            Grid.SetRow(body, 2);

            _dropZone = new Border
            {
                CornerRadius = new CornerRadius(14),
                // 拖放区：比周围更磨砂一点，形成"玻璃上的凹槽"
                Background = new LinearGradientBrush(
                    Color.FromArgb(30, 255, 255, 255),
                    Color.FromArgb(12, 255, 255, 255), 90),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                AllowDrop = true
            };
            body.Children.Add(_dropZone);

            var dropStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dropGlyph = ArrowGlyph(26, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
            dropGlyph.Margin = new Thickness(0, 0, 0, 10);
            var dropHint = new TextBlock
            {
                Text = "把压缩包拖到这里",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(245, 240, 242, 248)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var dropSub = new TextBlock
            {
                Text = "RAR · ZIP · 7Z · TAR · GZ",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(215, 210, 214, 226)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            dropStack.Children.Add(dropGlyph);
            dropStack.Children.Add(dropHint);
            dropStack.Children.Add(dropSub);
            _dropZone.Child = dropStack;

            // 进度面板（默认隐藏，覆盖在 dropZone 上）
            _progressHost = BuildProgressPanel();
            _progressHost.Visibility = Visibility.Collapsed;
            body.Children.Add(_progressHost);

            _dropZone.DragEnter += DropZone_DragOver;
            _dropZone.DragOver += DropZone_DragOver;
            _dropZone.DragLeave += (s, e) =>
            {
                _dropZone.BorderBrush = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
                _dropZone.Background = Glass.GlassFill(0.06, 0.02);
            };
            _dropZone.Drop += DropZone_Drop;
            _dropZone.MouseLeftButtonUp += (s, e) => { if (!_busy) PickArchive(); };

            content.Children.Add(body);

            // ---- 底栏 ----
            var foot = new Grid();
            Grid.SetRow(foot, 4);
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _mainBtn = BuildPrimaryButton("选择压缩包");
            _mainBtn.Click += (s, e) =>
            {
                if (_busy) { Cancel(); }
                else if (_pickedArchive != null && File.Exists(_pickedArchive)) { StartExtract(_pickedArchive); }
                else PickArchive();
            };
            Grid.SetColumn(_mainBtn, 0);
            foot.Children.Add(_mainBtn);

            // 输出目录：只在未进入预览时作为入口提示显示
            var destWrap = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                Cursor = Cursors.Hand,
                ToolTip = "点击更换输出目录"
            };
            var destIcon = new TextBlock
            {
                Text = "📁", FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextTertiary),
                VerticalAlignment = VerticalAlignment.Center
            };
            _destText = new TextBlock
            {
                Text = "输出：压缩包同级目录",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextTertiary),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 150
            };
            destWrap.Children.Add(destIcon);
            destWrap.Children.Add(_destText);
            destWrap.MouseLeftButtonUp += (s, e) => ChooseOutputDir();
            Grid.SetColumn(destWrap, 1);
            foot.Children.Add(destWrap);
            _mainDestWrap = destWrap;

            var histBtn = BuildGhostButton("历史");
            histBtn.Click += (s, e) => ToggleHistory();
            Grid.SetColumn(histBtn, 2);
            foot.Children.Add(histBtn);

            content.Children.Add(foot);

            // ---- 设置行：并行数 + 玻璃强度 ----
            var settings = BuildSettingsRow();
            Grid.SetRow(settings, 5);
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(settings);

            // 历史面板：浮在主区之上的覆盖层
            _histList = new ListBox
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Color.FromArgb(235, 22, 22, 28)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Margin = new Thickness(0),
                Padding = new Thickness(6),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_histList, ScrollBarVisibility.Auto);
            var histWrap = new Border
            {
                CornerRadius = new CornerRadius(12),
                Child = _histList,
                Margin = new Thickness(0, 4, 0, 4),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(histWrap, 2);
            Panel.SetZIndex(histWrap, 60);
            _histHost = histWrap;
            histWrap.Visibility = Visibility.Collapsed;
            body.Children.Add(histWrap);

            // 预览树面板：拖入压缩包后显示归档内容，可勾选单独解压
            _previewHost = BuildPreviewPanel();
            _previewHost.Visibility = Visibility.Collapsed;
            Panel.SetZIndex(_previewHost, 40);
            Grid.SetRow(_previewHost, 2);
            body.Children.Add(_previewHost);

            Content = outer;

            // 液态高光层
            var light = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(20),
                Margin = new Thickness(1)
            };
            var rg = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.28),
                GradientOrigin = new Point(0.5, 0.28),
                RadiusX = 0.75,
                RadiusY = 0.9
            };
            rg.GradientStops.Add(new GradientStop(Colors.White, 0));
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            light.OpacityMask = rg;
            light.Opacity = 0.055;
            light.Background = Brushes.White;
            Panel.SetZIndex(light, 100);
            outer.Children.Add(light);
            // 注意：原先这里挂了 LiquidLight.Attach 做"跟随鼠标的光斑"，
            // 但它在每次 MouseMove 都会重算全窗不透明遮罩 → 每次移动都触发整窗重绘，
            // 是拖动卡顿的元凶之一。玻璃氛围已由 Acrylic + 渐变层提供，这里不再跟随鼠标。

            if (_sevenZip == null)
            {
                _subText.Text = "未找到 7z.exe，请安装 7-Zip";
                _subText.Foreground = new SolidColorBrush(Theme.Orange);
            }
            else
            {
                _ext = new Extractor(_sevenZip);
            }
        }

        FrameworkElement BuildIcon()
        {
            var g = new Grid { Width = 26, Height = 26 };
            var ring = new Ellipse
            {
                Fill = new SolidColorBrush(Color.FromArgb(240, 10, 132, 255))
            };
            g.Children.Add(ring);
            g.Children.Add(ArrowGlyph(13, Brushes.White));
            return g;
        }

        /// <summary>用矢量画一个下箭头（避免依赖字体 glyph）。</summary>
        static FrameworkElement ArrowGlyph(double size, Brush brush)
        {
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(0.5, 0.08), IsClosed = false };
            fig.Segments.Add(new LineSegment(new Point(0.5, 0.62), true));
            geo.Figures.Add(fig);

            var head = new PathFigure { StartPoint = new Point(0.20, 0.40), IsClosed = false };
            head.Segments.Add(new LineSegment(new Point(0.5, 0.70), true));
            head.Segments.Add(new LineSegment(new Point(0.80, 0.40), true));
            geo.Figures.Add(head);

            var p = new ShapePath
            {
                Data = geo,
                Stroke = brush,
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return p;
        }

        /// <summary>矢量叉号（关闭按钮）。</summary>
        static FrameworkElement CrossGlyph(double size, Brush brush)
        {
            var geo = new PathGeometry();
            var a = new PathFigure { StartPoint = new Point(0.22, 0.22), IsClosed = false };
            a.Segments.Add(new LineSegment(new Point(0.78, 0.78), true));
            geo.Figures.Add(a);
            var b = new PathFigure { StartPoint = new Point(0.78, 0.22), IsClosed = false };
            b.Segments.Add(new LineSegment(new Point(0.22, 0.78), true));
            geo.Figures.Add(b);

            return new ShapePath
            {
                Data = geo,
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        Grid BuildProgressPanel()
        {
            var g = new Grid { Background = Brushes.Transparent };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 进度环
            var ringGrid = new Grid { Width = 116, Height = 116, HorizontalAlignment = HorizontalAlignment.Center };

            var baseRing = new Ellipse
            {
                Stroke = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                StrokeThickness = 7
            };
            ringGrid.Children.Add(baseRing);

            var arc = new ShapePath
            {
                Stroke = Glass.RingGradient(Accent, Accent2),
                StrokeThickness = 7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            ringGrid.Children.Add(arc);

            _ringPct = new TextBlock
            {
                Text = "0%",
                FontSize = 22,
                FontWeight = FontWeights.Light,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ringGrid.Children.Add(_ringPct);

            _progressHost = null; // 占位，实际在下方赋值
            _ringArc = arc;

            stack.Children.Add(ringGrid);

            _statLine = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Theme.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            };
            stack.Children.Add(_statLine);

            _fileText = new TextBlock
            {
                Text = "",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Theme.TextTertiary),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380
            };
            stack.Children.Add(_fileText);

            // 速度曲线
            _spark = new Canvas { Height = 26, Width = 200, Margin = new Thickness(0, 8, 0, 0) };
            stack.Children.Add(_spark);

            // ---- 实时查看：当前任务 + 输出流 ----
            var liveHead = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            liveHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            liveHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new Ellipse
            {
                Width = 5, Height = 5,
                Fill = new SolidColorBrush(Theme.Mint),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            liveHead.Children.Add(dot);

            _liveHead = new TextBlock
            {
                Text = "实时查看",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Theme.TextTertiary),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_liveHead, 1);
            liveHead.Children.Add(_liveHead);
            stack.Children.Add(liveHead);

            _liveBox = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 5, 0, 0),
                Padding = new Thickness(8, 5, 8, 5),
                Height = 74
            };
            _liveText = new TextBlock
            {
                Text = "等待开始…",
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(190, 150, 220, 255)),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _liveBox.Child = _liveText;
            stack.Children.Add(_liveBox);

            g.Children.Add(stack);
            return g;
        }

        TextBlock _liveHead;
        Border _liveBox;
        TextBlock _liveText;
        readonly Queue<string> _liveLines = new Queue<string>();

        /// <summary>把一条实时日志推入查看区（保留最近 5 行）。</summary>
        public void PushLive(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            _liveLines.Enqueue(line.Trim());
            while (_liveLines.Count > 5) _liveLines.Dequeue();
            _liveText.Text = string.Join("\n", _liveLines.ToArray());
        }

        ShapePath _ringArc;
        double _arcAngle = 0;

        void UpdateRing(double pct)
        {
            if (_ringArc == null) return;
            double angle = Math.Max(0.0001, pct * 360.0);
            _arcAngle = angle;
            double r = 54.0;
            double cx = 58, cy = 58;
            double rad = (angle - 90) * Math.PI / 180.0;
            double x = cx + r * Math.Cos(rad);
            double y = cy + r * Math.Sin(rad);
            bool large = angle > 180;

            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
            fig.Segments.Add(new ArcSegment
            {
                Point = new Point(x, y),
                Size = new Size(r, r),
                IsLargeArc = large,
                SweepDirection = SweepDirection.Clockwise
            });
            geo.Figures.Add(fig);
            _ringArc.Data = geo;
        }

        void DrawSpark()
        {
            if (_spark == null) return;
            _spark.Children.Clear();
            if (_speedSamples.Count < 2) return;

            double w = _spark.Width, h = _spark.Height;
            double max = 1;
            foreach (var v in _speedSamples) if (v > max) max = v;

            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(170, 100, 210, 255)),
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round
            };
            int n = _speedSamples.Count;
            for (int i = 0; i < n; i++)
            {
                double x = w * i / (n - 1.0);
                double y = h - (h - 3) * (_speedSamples[i] / max);
                poly.Points.Add(new Point(x, y));
            }
            _spark.Children.Add(poly);
        }

        // =================================================================
        // 控件工厂
        // =================================================================

        Button BuildPrimaryButton(string text)
        {
            var b = new Button
            {
                Content = text,
                FontSize = 12,
                Foreground = Brushes.White,
                Padding = new Thickness(16, 7, 16, 7),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Background = new SolidColorBrush(Color.FromArgb(255, 10, 132, 255))
            };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            b.Template = tpl;

            b.MouseEnter += (s, e) => b.Opacity = 0.86;
            b.MouseLeave += (s, e) => b.Opacity = 1.0;
            return b;
        }

        Button BuildGhostButton(string text)
        {
            var b = new Button
            {
                Content = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextSecondary),
                Padding = new Thickness(12, 6, 12, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255))
            };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            b.Template = tpl;
            b.MouseEnter += (s, e) => b.Opacity = 0.8;
            b.MouseLeave += (s, e) => b.Opacity = 1.0;
            return b;
        }

        Button BuildCircleButton(string glyph, double size)
        {
            var b = new Button
            {
                Content = glyph,
                FontSize = size,
                Width = 22, Height = 22,
                Foreground = new SolidColorBrush(Theme.TextTertiary),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0)
            };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            bd.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            b.Template = tpl;
            b.MouseEnter += (s, e) =>
            {
                b.Foreground = new SolidColorBrush(Theme.TextPrimary);
                b.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            };
            b.MouseLeave += (s, e) =>
            {
                b.Foreground = new SolidColorBrush(Theme.TextTertiary);
                b.Background = Brushes.Transparent;
            };
            return b;
        }

        void AnimateIn()
        {
            // 透明窗体的入场：轻微缩放 + 淡入
            _rootCard.RenderTransformOrigin = new Point(0.5, 0.5);
            _rootCard.RenderTransform = new ScaleTransform(0.97, 0.97);
            var st = (ScaleTransform)_rootCard.RenderTransform;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _rootCard.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
        }

        // =================================================================
        // 拖放 / 选择
        // =================================================================

        void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (_busy) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                bool ok = files != null && files.Length > 0 && Extractor.IsArchive(files[0]);
                e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
                if (ok)
                {
                    _dropZone.BorderBrush = new SolidColorBrush(Color.FromArgb(170, 100, 210, 255));
                    _dropZone.Background = Glass.GlassFill(0.12, 0.05);
                }
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (_busy) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            string first = files[0];
            if (!Extractor.IsArchive(first))
            {
                _subText.Text = "不支持的格式";
                _subText.Foreground = new SolidColorBrush(Theme.Orange);
                return;
            }
            StartExtract(first);
        }

        void PickArchive()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择压缩包",
                Filter = "压缩包|*.rar;*.zip;*.7z;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.cab;*.iso|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _pickedArchive = dlg.FileName;
                StartExtract(dlg.FileName);
            }
        }

        string _customDest;

        void ChooseOutputDir()
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择解压输出目录",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _customDest = dlg.SelectedPath;
                _destText.Text = "输出：" + Shorten(_customDest);
                _destText.Foreground = new SolidColorBrush(Theme.TextSecondary);
            }
        }

        static string Shorten(string p)
        {
            if (p == null) return "";
            if (p.Length <= 30) return p;
            return "…" + p.Substring(p.Length - 28);
        }

        // =================================================================
        // 解压流程
        // =================================================================

        /// <summary>拖入或选择压缩包后：读清单 → 显示预览树（不立刻解压）。</summary>
        async void StartExtract(string archive)
        {
            if (_ext == null)
            {
                _subText.Text = "缺少 7z.exe，无法解压";
                _subText.Foreground = new SolidColorBrush(Theme.Red);
                return;
            }
            _pickedArchive = archive;
            await LoadPreview(archive);
        }

        /// <summary>读取清单并构建预览树。</summary>
        async Task LoadPreview(string archive)
        {
            _busy = true;
            _titleText.Text = Path.GetFileName(archive);
            _subText.Text = "正在读取压缩包…";
            _subText.Foreground = new SolidColorBrush(Theme.TextTertiary);
            SetMainMode(MainMode.Preview);

            var cts = new CancellationTokenSource();
            try
            {
                var entries = await Task.Run(() => _ext.ListArchive(archive, cts.Token), cts.Token);

                long total = 0;
                foreach (var e in entries) total += e.Size;

                _entries = entries;
                _treeRoot = ArcNode.BuildTree(entries);
                ArcNode.ExpandTop(_treeRoot, 1);
                _tree.ItemsSource = _treeRoot;

                _previewSummary.Text = string.Format("压缩包内容 · {0} 项 · {1}",
                    entries.Count, FormatSize(total));

                _subText.Text = string.Format("{0} 项 · {1} · 勾选后单独解压",
                    entries.Count, FormatSize(total));
                _subText.Foreground = new SolidColorBrush(Theme.TextTertiary);

                _mainBtn.Content = "全部解压";
                _mainBtn.Background = new SolidColorBrush(Color.FromArgb(255, 10, 132, 255));
                _mainBtn.Click -= MainBtnAllHandler;
                _mainBtn.Click += MainBtnAllHandler;

                UpdateSelButton();
            }
            catch (Exception ex)
            {
                _subText.Text = "读取失败";
                _subText.Foreground = new SolidColorBrush(Theme.Red);
                _statLine.Text = ex.Message;
            }
            finally
            {
                _busy = false;
                cts.Dispose();
            }
        }

        void MainBtnAllHandler(object sender, RoutedEventArgs e)
        {
            // 全部解压：不传选中集合
            var t = RunExtract(_pickedArchive, null, false);
        }

        /// <summary>预览之后开始真正解压。selected 为 null 表示全部。</summary>
        async void StartExtractSelected(List<string> selected)
        {
            await RunExtract(_pickedArchive, selected, true);
        }

        /// <summary>核心解压流程。selected 为 null 时解压整个包。</summary>
        async Task RunExtract(string archive, List<string> selected, bool partial)
        {
            if (_ext == null || archive == null) return;
            if (_busy) return;

            _pickedArchive = archive;
            _busy = true;
            _speedSamples.Clear();
            _cts = new CancellationTokenSource();

            string dest = _customDest ?? Path.GetDirectoryName(archive);
            SetMainMode(MainMode.Progress);
            _mainBtn.Content = "取消";
            _mainBtn.Background = new SolidColorBrush(Color.FromArgb(220, 255, 69, 58));
            _mainBtn.Click -= MainBtnAllHandler;
            _titleText.Text = Path.GetFileName(archive);
            _subText.Text = partial ? "正在准备选中的文件…" : "正在分析压缩包…";
            _subText.Foreground = new SolidColorBrush(Theme.TextTertiary);

            var ct = _cts.Token;
            var sw = Stopwatch.StartNew();
            int conc = EffectiveConcurrency();

            try
            {
                var entries = _entries;
                if (entries == null)
                    entries = await Task.Run(() => _ext.ListArchive(archive, ct), ct);

                long totalBytes = 0;
                if (partial)
                {
                    var want = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                    foreach (var e in entries)
                        if (want.Contains(Extractor.NormalizeArcPath(e.Path))) totalBytes += e.Size;
                }
                else
                {
                    foreach (var e in entries) totalBytes += e.Size;
                }

                string work = Path.Combine(Path.GetTempPath(), "liquidunzip_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                long bigThreshold = Math.Max(300L * 1024 * 1024, totalBytes / 200);

                List<Extractor.Job> jobs;
                if (partial)
                    jobs = await Task.Run(() => _ext.PlanSelectedJobs(entries, selected, work, conc, bigThreshold, ct), ct);
                else
                    jobs = await Task.Run(() => _ext.PlanJobs(entries, work, conc, bigThreshold, ct), ct);

                _subText.Text = string.Format("{0} · {1} · {2} 路并行",
                    partial ? ("已选 " + selected.Count + " 个文件") : (entries.Count + " 个文件"),
                    FormatSize(totalBytes), conc);

                var progress = new Progress<Extractor.Progress>(p =>
                {
                    double pct = p.Percent;
                    _ringPct.Text = ((int)(pct * 100)) + "%";
                    UpdateRing(pct);
                    _statLine.Text = string.Format("{0} / {1}   ·   {2} MB/s   ·   {3}",
                        FormatSize(p.BytesDone), FormatSize(p.BytesTotal), p.MBps.ToString("0.0"),
                        FormatDur(p.ElapsedSec));
                    _fileText.Text = p.CurrentFile ?? "";
                    _speedSamples.Add(p.MBps);
                    if (_speedSamples.Count > 60) _speedSamples.RemoveAt(0);
                    DrawSpark();
                });

                await Task.Run(() => _ext.RunJobs(jobs, archive, dest, conc,
                    p => ((IProgress<Extractor.Progress>)progress).Report(p),
                    live => Dispatcher.BeginInvoke(new Action(() => PushLive(live))),
                    ct), ct);

                sw.Stop();
                _ringPct.Text = "100%";
                UpdateRing(1.0);
                _statLine.Text = string.Format("完成 · {0} · 用时 {1} · 平均 {2} MB/s",
                    FormatSize(totalBytes), FormatDur((int)sw.Elapsed.TotalSeconds),
                    (totalBytes / 1048576.0 / Math.Max(1, sw.Elapsed.TotalSeconds)).ToString("0.0"));
                _fileText.Text = "解压到 " + dest;
                _subText.Text = partial ? "选中项已解压" : "解压完成";

                AddHistory(new HistoryItem
                {
                    ArchivePath = archive,
                    DestPath = dest,
                    When = DateTime.Now,
                    Bytes = totalBytes,
                    Seconds = (int)sw.Elapsed.TotalSeconds
                });

                try { Directory.Delete(work, true); } catch { }

                _mainBtn.Content = "打开文件夹";
                _mainBtn.Background = new SolidColorBrush(Color.FromArgb(255, 48, 209, 88));
                _mainBtn.Click -= OpenDestHandler;
                _mainBtn.Click += OpenDestHandler;
                _lastDest = dest;
            }
            catch (OperationCanceledException)
            {
                _subText.Text = "已取消";
                _subText.Foreground = new SolidColorBrush(Theme.Orange);
                _statLine.Text = "解压已中止";
                ResetButton();
            }
            catch (Exception ex)
            {
                _subText.Text = "解压失败";
                _subText.Foreground = new SolidColorBrush(Theme.Red);
                _statLine.Text = ex.Message.Length > 80 ? ex.Message.Substring(0, 80) + "…" : ex.Message;
                ResetButton();
            }
            finally
            {
                _busy = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
            }
        }

        string _lastDest;

        /// <summary>由命令行参数（拖到 exe 图标）触发解压。</summary>
        public void BeginExtractFromArgs(string archive)
        {
            StartExtract(archive);
        }

        /// <summary>统一切换主区显示状态，避免各处重复设置漏掉某个元素。</summary>
        void SetMainMode(MainMode mode)
        {
            _dropZone.Visibility = mode == MainMode.Idle ? Visibility.Visible : Visibility.Collapsed;
            _previewHost.Visibility = mode == MainMode.Preview ? Visibility.Visible : Visibility.Collapsed;
            _progressHost.Visibility = mode == MainMode.Progress ? Visibility.Visible : Visibility.Collapsed;
            if (_mainDestWrap != null)
                _mainDestWrap.Visibility = mode == MainMode.Preview
                    ? Visibility.Collapsed : Visibility.Visible;
        }

        enum MainMode { Idle, Preview, Progress }

        /// <summary>调试用：把当前预览树打到控制台，证明显示的是真实数据。</summary>
        public void DumpTree()
        {
            Console.WriteLine("=== 预览树实际内容 ===");
            if (_treeRoot == null) { Console.WriteLine("(空)"); return; }
            DumpNodes(_treeRoot, 0);
            Console.WriteLine("哈希/勾选按钮状态: " + (_extractSelBtn != null ? _extractSelBtn.Content : "null"));
        }

        static void DumpNodes(System.Collections.IEnumerable nodes, int depth)
        {
            if (nodes == null) return;
            foreach (ArcNode n in nodes)
            {
                Console.WriteLine(new string(' ', depth * 2) +
                    n.Glyph + " " + n.Name + "  [" + n.SizeText + "]  " + n.GlyphColor);
                if (n.Children != null) DumpNodes(n.Children, depth + 1);
            }
        }

        /// <summary>仅供截图工具：填充预览树演示数据。</summary>
        public void ShowDemoPreview()
        {
            SetMainMode(MainMode.Preview);
            _titleText.Text = "ComfyUI-Minimax_h3.rar";
            _subText.Text = "104 项 · 225 GB · 勾选后单独解压";

            var entries = new List<Extractor.Entry>();
            string[] names = {
                "ComfyUI/models/diffusion_models/minimax_h3_fl2va_pruned_int8.safetensors",
                "ComfyUI/models/diffusion_models/minimax_h3_ref2va_pruned_int8.safetensors",
                "ComfyUI/models/text_encoders/qwen3vl_32b_minimax_h3_int8.safetensors",
                "ComfyUI/models/unet/flux-2-klein-9b-fp8.safetensors",
                "ComfyUI/models/vae/minimax_h3_video_vae_fp16.safetensors",
                "ComfyUI/models/vae/minimax_h3_audio_vae_fp32.safetensors",
                "ComfyUI/custom_nodes/ComfyUI-Manager/__init__.py",
                "ComfyUI/custom_nodes/ComfyUI-Manager/manager_server.py",
                "ComfyUI/custom_nodes/Comfyui-zhenzhen/workflow/seedance2.0.json",
                "ComfyUI/main.py",
                "ComfyUI/comfy/ldm/minimax/model.py",
                "python_embeded/python.exe"
            };
            long[] sizes = {
                20970379616, 20970379616, 27141342152, 9439134848,
                5207808496, 601356288, 12480, 184320, 98304,
                72192, 41984, 106496
            };
            for (int i = 0; i < names.Length; i++)
                entries.Add(new Extractor.Entry { Path = names[i].Replace('/', '\\'), Size = sizes[i] });

            _entries = entries;
            _treeRoot = ArcNode.BuildTree(entries);
            ArcNode.ExpandTop(_treeRoot, 2);
            _tree.ItemsSource = _treeRoot;
            _previewSummary.Text = "压缩包内容 · 12 项 · 97.4 GB";

            // 演示：勾选两个大模型
            foreach (var n in _treeRoot)
                MarkByName(n, "minimax_h3_fl2va_pruned_int8.safetensors");
            foreach (var n in _treeRoot)
                MarkByName(n, "qwen3vl_32b_minimax_h3_int8.safetensors");

            _mainBtn.Content = "全部解压";
            _mainBtn.Click -= MainBtnAllHandler;
            _mainBtn.Click += MainBtnAllHandler;
            UpdateSelButton();
        }

        static bool MarkByName(ArcNode n, string name)
        {
            if (n.Name == name && !n.IsFolder) { n.IsChecked = true; return true; }
            if (n.Children != null)
                foreach (var c in n.Children)
                    if (MarkByName(c, name)) return true;
            return false;
        }

        /// <summary>仅供截图工具：切到横屏并显示预览。</summary>
        public void ShowDemoWide()
        {
            ShowDemoPreview();
            ToggleWide();
        }
        public void ShowDemoProgress()
        {
            _dropZone.Visibility = Visibility.Collapsed;
            _progressHost.Visibility = Visibility.Visible;
            _titleText.Text = "ComfyUI-Minimax_h3.rar";
            _subText.Text = "8 个文件 · 28.6 GB · 8 路并行";
            _subText.Foreground = new SolidColorBrush(Theme.TextTertiary);
            _mainBtn.Content = "取消";
            _mainBtn.Background = new SolidColorBrush(Color.FromArgb(220, 255, 69, 58));

            double pct = 0.62;
            _ringPct.Text = "62%";
            UpdateRing(pct);
            _statLine.Text = "17.7 GB / 28.6 GB   ·   186.4 MB/s   ·   1 分 37 秒";
            _fileText.Text = "minimax_h3_fl2va_pruned_int8_convrot.safetensors";

            double[] demo = { 92, 118, 141, 155, 149, 162, 178, 171, 186, 180, 192, 186, 190, 184 };
            foreach (var v in demo) _speedSamples.Add(v);
            DrawSpark();

            // 演示实时查看区
            PushLive("Extracting  minimax_h3_fl2va_pruned_int8_convrot.safetensors");
            PushLive(" 47% - minimax_h3_fl2va_pruned_int8_convrot.safetensors");
            PushLive(" 63% - minimax_h3_ref2va_pruned_int8_convrot.safetensors");
            PushLive(" 88% - qwen3vl_32b_minimax_h3_int8_convrot.safetensors");
            PushLive(" 99% - z_image_turbo_bf16.safetensors");
        }

        void OpenDestHandler(object sender, RoutedEventArgs e)
        {
            try { Process.Start("explorer.exe", "\"" + _lastDest + "\""); } catch { }
            ResetButton();
        }

        void ResetButton()
        {
            _mainBtn.Click -= OpenDestHandler;
            _mainBtn.Content = "选择压缩包";
            _mainBtn.Background = new SolidColorBrush(Color.FromArgb(255, 10, 132, 255));
            SetMainMode(MainMode.Idle);
            _titleText.Text = "Liquid Unzip";
        }

        void Cancel()
        {
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
        }

        // =================================================================
        // 历史
        // =================================================================

        static string HistoryPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiquidUnzip");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "history.txt");
            }
        }

        void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                foreach (var line in File.ReadAllLines(HistoryPath, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length < 5) continue;
                    DateTime when; long bytes; int secs;
                    DateTime.TryParse(p[2], out when);
                    long.TryParse(p[3], out bytes);
                    int.TryParse(p[4], out secs);
                    if (when == default(DateTime)) when = DateTime.Now;
                    _history.Add(new HistoryItem
                    {
                        ArchivePath = p[0],
                        DestPath = p[1],
                        When = when,
                        Bytes = bytes,
                        Seconds = secs
                    });
                }
            }
            catch { }
        }

        void SaveHistory()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var h in _history)
                    sb.AppendLine(string.Join("\t", h.ArchivePath, h.DestPath,
                        h.When.ToString("o"), h.Bytes, h.Seconds));
                File.WriteAllText(HistoryPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        void AddHistory(HistoryItem h)
        {
            _history.Insert(0, h);
            if (_history.Count > 12) _history.RemoveRange(12, _history.Count - 12);
            SaveHistory();
        }

        bool _histOpen;

        void ToggleHistory()
        {
            _histOpen = !_histOpen;
            if (!_histOpen) { _histHost.Visibility = Visibility.Collapsed; return; }

            _histList.Items.Clear();
            if (_history.Count == 0)
            {
                _histList.Items.Add(new TextBlock
                {
                    Text = "还没有解压记录",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Theme.TextTertiary),
                    Margin = new Thickness(8, 8, 0, 8)
                });
            }
            else
            {
                foreach (var h in _history) _histList.Items.Add(BuildHistoryRow(h));
            }
            _histList.MaxHeight = Math.Min(240, Math.Max(60, _history.Count * 48 + 16));
            _histHost.Visibility = Visibility.Visible;
        }

        FrameworkElement BuildHistoryRow(HistoryItem h)
        {
            var g = new Grid { Margin = new Thickness(2, 2, 2, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var st = new StackPanel();
            st.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(h.ArchivePath),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 360
            });
            st.Children.Add(new TextBlock
            {
                Text = string.Format("{0} · {1} · {2}",
                    h.When.ToString("MM-dd HH:mm"), FormatSize(h.Bytes), FormatDur(h.Seconds)),
                FontSize = 10,
                Foreground = new SolidColorBrush(Theme.TextTertiary),
                Margin = new Thickness(0, 1, 0, 0)
            });
            g.Children.Add(st);

            var go = BuildCircleButton("↗", 11);
            go.ToolTip = "打开解压目录";
            go.Click += (s, e) =>
            {
                try { Process.Start("explorer.exe", "\"" + h.DestPath + "\""); } catch { }
            };
            Grid.SetColumn(go, 1);
            g.Children.Add(go);

            var wrap = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 8, 5),
                Background = Brushes.Transparent,
                Child = g,
                Cursor = Cursors.Hand
            };
            wrap.MouseEnter += (s, e) => wrap.Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
            wrap.MouseLeave += (s, e) => wrap.Background = Brushes.Transparent;
            return wrap;
        }

        // =================================================================
        // 格式化
        // =================================================================

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            double gb = mb / 1024.0;
            if (gb < 1024) return gb.ToString("0.00") + " GB";
            return (gb / 1024.0).ToString("0.00") + " TB";
        }

        public static string FormatDur(int sec)
        {
            if (sec < 60) return sec + " 秒";
            int m = sec / 60, s = sec % 60;
            if (m < 60) return m + " 分 " + s + " 秒";
            return (m / 60) + " 时 " + (m % 60) + " 分";
        }
    }
}
