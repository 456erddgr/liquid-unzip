using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Image = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LiquidUnzip
{
    /// <summary>
    /// 液态玻璃表面。
    ///
    /// 性能设计（这版重写的核心）：
    ///   - 背板图先降采样到 256px 以内（见 Backdrop），模糊成本随像素量骤降。
    ///   - 只用 **一层** BlurEffect，模糊半径小（12–20）。原来两层大半径模糊是卡顿主因。
    ///   - "不同区域磨砂度不同"靠**遮罩过渡**实现：底层清晰图与模糊图之间用渐变
    ///     透明度混合，而不是再用第二层模糊。视觉等效，开销减半以上。
    ///   - 只用 RenderTransform（GPU 合成），不动 LayoutTransform。
    /// </summary>
    public class LiquidGlassSurface
    {
        public Grid Host { get; private set; }

        Image _sharp;      // 清晰层（低模糊）
        Image _blurred;    // 磨砂层（高模糊），用遮罩做局部显现
        Rectangle _tint;
        Rectangle _sheen;
        BlurEffect _blurFx;

        public LiquidGlassSurface()
        {
            Host = new Grid { ClipToBounds = true, IsHitTestVisible = false };

            // 清晰层：只在"实时背景"模式下有内容（系统 Acrylic 模式下为空）
            _sharp = new Image { Stretch = Stretch.UniformToFill };
            Host.Children.Add(_sharp);

            // 磨砂层：唯一的一层模糊，只在实时背景模式下启用
            _blurFx = new BlurEffect { Radius = 16, RenderingBias = RenderingBias.Performance };
            _blurred = new Image
            {
                Stretch = Stretch.UniformToFill,
                Effect = _blurFx,
                Opacity = 0
            };
            var mask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0.28, 1)
            };
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.00));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.28));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(195, 0, 0, 0), 0.58));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.85));
            mask.Freeze();
            _blurred.OpacityMask = mask;
            Host.Children.Add(_blurred);

            // 磨砂渐变装饰层：即使没有采集，也用渐变做出"上清下浑"的观感。
            // 这是纯矢量渐变，GPU 合成，零性能代价。
            // 注意：整体要足够淡，否则会把 Acrylic 透出来的桌面盖死，看起来就像一块黑底。
            var frost = new Rectangle();
            var fb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.25, 1) };
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(0, 8, 10, 16), 0.00));
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(14, 8, 10, 16), 0.30));
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(44, 6, 8, 14), 0.62));
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(66, 4, 6, 12), 0.90));
            fb.Freeze();
            frost.Fill = fb;
            Host.Children.Add(frost);

            // 色调层：压住白字对比度（保持很淡，让桌面透出来）
            _tint = new Rectangle
            {
                Fill = new LinearGradientBrush(
                    Color.FromArgb(38, 10, 12, 18),
                    Color.FromArgb(62, 6, 8, 14), 90)
            };
            Host.Children.Add(_tint);

            // 厚度层：左上到右下极淡白
            var sheenBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            sheenBrush.GradientStops.Add(new GradientStop(Color.FromArgb(30, 255, 255, 255), 0));
            sheenBrush.GradientStops.Add(new GradientStop(Color.FromArgb(9, 255, 255, 255), 0.35));
            sheenBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.7));
            sheenBrush.Freeze();
            _sheen = new Rectangle { Fill = sheenBrush };
            Host.Children.Add(_sheen);

            _frostDeco = frost;
        }

        Rectangle _frostDeco;

        /// <summary>喂入最新背板帧（仅实时背景模式）。</summary>
        public void SetFrame(ImageSource frame)
        {
            if (frame == null) return;
            _sharp.Source = frame;
            _blurred.Source = frame;
        }

        /// <summary>
        /// 拖动/缩放期间把玻璃"冻住"：把模糊层置灰到极低开销，
        /// 避免每帧对整窗重新做卷积。松手后恢复。
        /// </summary>
        public void Freeze(bool frozen)
        {
            if (_blurFx == null) return;
            // 冻结时把模糊半径压到 0 —— WPF 会跳过 effect 通道，几乎零开销
            _blurFx.Radius = frozen ? 0 : _activeRadius;
            _blurred.Opacity = frozen ? _activeOpacity * 0.6 : _activeOpacity;
        }

        double _activeRadius = 16;
        double _activeOpacity = 0.9;

        /// <summary>磨砂强度 0..1：调节磨砂渐变装饰的浓淡与 Acrylic 色调。</summary>
        public void ApplyStrength(double s)
        {
            s = Math.Max(0, Math.Min(1, s));
            _activeRadius = 5 + 15 * s;
            _activeOpacity = 0.55 + 0.45 * s;
            if (_blurFx != null) _blurFx.Radius = _activeRadius;

            // 实时背景模式下才让模糊层显现
            if (_liveMode) _blurred.Opacity = _activeOpacity;

            // 渐变装饰：强度越高越浑
            if (_frostDeco != null) _frostDeco.Opacity = 0.35 + 0.85 * s;

            _tint.Opacity = 0.55 + 0.45 * s;
        }

        bool _liveMode;

        /// <summary>切换"实时背景"模式（逐帧采集）。</summary>
        public void SetLiveMode(bool live)
        {
            _liveMode = live;
            _blurred.Opacity = live ? _activeOpacity : 0;
            if (_frostDeco != null) _frostDeco.Opacity = live ? 0.25 : 0.85;
        }
    }
}
