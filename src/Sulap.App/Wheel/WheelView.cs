using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Sulap.App.Platform;
using Sulap.Core.Formats;
using Sulap.Core.Wheels;

namespace Sulap.App.Wheel;

/// <summary>What the hub says: a caption over a title, and optionally a few lines of detail.</summary>
/// <param name="TitleOnPill">Draws the title on an accent-colored pill: it names what a drop will do.</param>
internal sealed record HubText(string Caption, string Title, string? Detail = null, bool TitleOnPill = false);

/// <summary>What the wheel shows for one drag.</summary>
/// <param name="Hub">The hub's text while no wedge is under the pointer.</param>
/// <param name="WedgeHubs">The hub's text while each wedge is under the pointer.</param>
internal sealed record WheelContent(IReadOnlyList<ConvertOption> Options, HubText Hub, IReadOnlyList<HubText> WedgeHubs)
{
    /// <summary>No wedges: only a card that says why the dragged files can't be converted. A drop on it does nothing.</summary>
    public bool IsNotice => Options.Count == 0;
}

/// <summary>The wheel's colors, from the Windows app theme and accent color.</summary>
/// <param name="Accent">The hovered wedge. As on WinUI's accent buttons: a light shade on dark, a dark shade on light.</param>
/// <param name="OnAccent">Text on <paramref name="Accent"/>.</param>
internal sealed record WheelTheme(bool IsDark, Color Accent, Color OnAccent)
{
    public static WheelTheme Current() => For(dark: !WindowsTheme.AppsUseLightTheme);

    public static WheelTheme For(bool dark) => dark
        ? new WheelTheme(IsDark: true, SystemColors.AccentColorLight2, Colors.Black)
        : new WheelTheme(IsDark: false, SystemColors.AccentColorDark1, Colors.White);
}

/// <summary>
/// Draws the Convert wheel: a rounded wedge per option around a hub that says what a drop will do.
/// Everything comes from <see cref="WheelLayout"/>, the same geometry the hit test uses. Until the
/// dragged files are known it's an invisible sensor: every pixel has alpha 1, which is enough for
/// Windows to send it the drag and too little to see.
/// </summary>
internal sealed class WheelView : FrameworkElement
{
    public const double OuterRadius = 140;
    public const double InnerRadius = 72;
    public const double WindowSize = 2 * (OuterRadius + ShadowSize);

    private const double ShadowSize = 20;
    private const double Gap = 4;
    private const double Corner = 6;
    private const double HubRadius = InnerRadius - 6;
    private const double NoticeRadius = 100;
    private const double LabelRadius = (InnerRadius + OuterRadius) / 2;

    public static readonly DependencyProperty AppearProperty = DependencyProperty.Register(
        nameof(Appear), typeof(double), typeof(WheelView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");

    private readonly Dictionary<TextKey, (FormattedText Text, Rect Ink)> _texts = [];
    private WheelContent? _content;
    private WheelLayout _layout = new(0, InnerRadius, OuterRadius);
    private Geometry[] _wedges = [];
    private Palette _palette = Palette.For(WheelTheme.Current());
    private int _hovered = -1;
    private bool _closing;
    private int _animation;

    public WheelView()
    {
        Width = WindowSize;
        Height = WindowSize;
        Focusable = false;
    }

    /// <summary>0 when hidden, 1 when fully shown; animated on the way in and out.</summary>
    public double Appear
    {
        get => (double)GetValue(AppearProperty);
        set => SetValue(AppearProperty, value);
    }

    /// <summary>What's shown, or null while this is the sensor.</summary>
    public WheelContent? Shown => _content;

    public int Hovered => _hovered;

    /// <summary>Back to the invisible sensor, ready for the next drag.</summary>
    public void ShowSensor()
    {
        StopAnimation();
        _content = null;
        _hovered = -1;
        _closing = false;
        Appear = 0;
        InvalidateVisual();
    }

    public void ShowWheel(WheelContent content, WheelTheme theme, bool animate)
    {
        StopAnimation();
        _content = content;
        _palette = Palette.For(theme);
        _texts.Clear();
        _layout = new WheelLayout(content.Options.Count, InnerRadius, OuterRadius);
        _wedges = [.. Enumerable.Range(0, content.Options.Count).Select(BuildWedge)];
        _hovered = -1;
        _closing = false;
        Appear = animate ? 0 : 1;
        if (animate)
        {
            Animate(1, TimeSpan.FromMilliseconds(220), completed: null);
        }

        InvalidateVisual();
    }

    /// <summary>Fades the wheel out, then calls <paramref name="closed"/>.</summary>
    public void Close(bool animate, Action closed)
    {
        if (_content is null || !animate)
        {
            StopAnimation();
            Appear = 0;
            closed();
            return;
        }

        _closing = true;
        Animate(0, TimeSpan.FromMilliseconds(130), closed);
    }

    /// <summary>Which part of the wheel a point in this element's coordinates is over.</summary>
    public WheelHit WheelHitAt(Point point) =>
        _content is null ? WheelHit.Outside : _layout.HitTest(point.X - (ActualWidth / 2), point.Y - (ActualHeight / 2));

    /// <summary>The middle of a wedge, in this element's coordinates.</summary>
    public Point WedgeCenter(int wedge)
    {
        var point = _layout.LabelPoint(wedge, LabelRadius);
        return new Point((ActualWidth / 2) + point.X, (ActualHeight / 2) + point.Y);
    }

    public void SetHovered(int wedge)
    {
        if (wedge != _hovered)
        {
            _hovered = wedge;
            InvalidateVisual();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _texts.Clear(); // Text is laid out for the old pixel density.
        base.OnDpiChanged(oldDpi, newDpi);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(RenderSize);
        if (_content is not { } content)
        {
            drawing.DrawRectangle(HitBrush, null, bounds);
            return;
        }

        drawing.PushTransform(new TranslateTransform(bounds.Width / 2, bounds.Height / 2));
        // The whole disk stays hit-testable at full size whatever the animation shows: Windows lets a
        // drag fall through pixels with alpha 0, and the gaps between wedges count as the nearest wedge.
        // A notice is a smaller card, and only that much of the screen is taken.
        var hitRadius = (content.IsNotice ? NoticeRadius : OuterRadius) + 1;
        drawing.DrawEllipse(HitBrush, null, default, hitRadius, hitRadius);

        var t = Math.Clamp(Appear, 0, 1);
        var (scale, opacity) = _closing
            ? (0.94 + (0.06 * t), t)
            : (0.84 + (0.16 * EaseOutBack(t)), EaseOutCubic(Math.Min(1, t / 0.6)));
        if (opacity > 0.004)
        {
            drawing.PushTransform(new ScaleTransform(scale, scale));
            drawing.PushOpacity(opacity);
            DrawWheel(drawing, content);
            drawing.Pop();
            drawing.Pop();
        }

        drawing.Pop();
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>Overshoots a little before settling, for a slight pop.</summary>
    private static double EaseOutBack(double t)
    {
        const double overshoot = 1.6;
        var u = t - 1;
        return 1 + ((overshoot + 1) * u * u * u) + (overshoot * u * u);
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Point ToPoint(WheelPoint point) => new(point.X, point.Y);

    private void Animate(double to, TimeSpan duration, Action? completed)
    {
        // Replacing an animation doesn't reliably stop the old one's Completed event; the version does.
        var version = ++_animation;
        var animation = new DoubleAnimation(to, new Duration(duration));
        if (completed is not null)
        {
            animation.Completed += (_, _) =>
            {
                if (version == _animation)
                {
                    completed();
                }
            };
        }

        BeginAnimation(AppearProperty, animation);
    }

    private void StopAnimation()
    {
        _animation++;
        BeginAnimation(AppearProperty, null);
    }

    private void DrawWheel(DrawingContext drawing, WheelContent content)
    {
        var palette = _palette;
        if (content.IsNotice)
        {
            // The wheel's own parts, scaled down to a card: same shadow, rim, and light.
            var k = NoticeRadius / OuterRadius;
            drawing.PushTransform(new ScaleTransform(k, k));
            drawing.DrawEllipse(palette.Shadow, null, new Point(0, 5), OuterRadius + ShadowSize, OuterRadius + ShadowSize);
            drawing.DrawEllipse(palette.Hub, palette.Rim, default, OuterRadius - 0.5, OuterRadius - 0.5);
            drawing.DrawEllipse(palette.Sheen, null, default, OuterRadius - 0.5, OuterRadius - 0.5);
            drawing.Pop();
            DrawHub(drawing, content.Hub, NoticeRadius);
            return;
        }

        drawing.DrawEllipse(palette.Shadow, null, new Point(0, 5), OuterRadius + ShadowSize, OuterRadius + ShadowSize);
        // A plate under the wedges: the gaps read as grooves, not as holes onto whatever is behind.
        drawing.DrawEllipse(palette.Plate, null, default, OuterRadius - 0.5, OuterRadius - 0.5);
        for (var i = 0; i < content.Options.Count; i++)
        {
            var option = content.Options[i];
            var hovered = i == _hovered;
            var active = hovered && option.IsEnabled;
            drawing.DrawGeometry(active ? palette.Accent : hovered ? palette.SurfaceHover : palette.Surface, null, _wedges[i]);
            drawing.DrawGeometry(active ? palette.AccentSheen : palette.Sheen, palette.Rim, _wedges[i]);

            var brush = active ? palette.OnAccent : option.IsEnabled ? palette.Text : palette.TextDisabled;
            var label = Text(option.Label, DisplayFont, 15, FontWeights.SemiBold, brush);
            var at = _layout.LabelPoint(i, LabelRadius);
            // Center the ink, not the line box: the labels are capitals, with no descenders.
            drawing.DrawText(label.Text, new Point(at.X - label.Ink.X - (label.Ink.Width / 2), at.Y - label.Ink.Y - (label.Ink.Height / 2)));
        }

        drawing.DrawEllipse(palette.Hub, palette.Rim, default, HubRadius, HubRadius);
        drawing.DrawEllipse(palette.Sheen, null, default, HubRadius, HubRadius);
        DrawHub(drawing, _hovered >= 0 && _hovered < content.WedgeHubs.Count ? content.WedgeHubs[_hovered] : content.Hub);
    }

    private void DrawHub(DrawingContext drawing, HubText hub, double radius = HubRadius)
    {
        const double spacing = 3;
        const double pillX = 10;
        const double pillY = 2;
        var palette = _palette;
        var width = (radius * 2) - 26; // Clear of the circle's curve.
        var caption = Text(hub.Caption, TextFont, 12, FontWeights.Normal, palette.TextSecondary, width).Text;
        var title = hub.TitleOnPill
            ? Text(hub.Title, DisplayFont, 14, FontWeights.SemiBold, palette.OnAccent, width - (2 * pillX)).Text
            : Text(hub.Title, DisplayFont, 15, FontWeights.SemiBold, palette.Text, width).Text;
        var detail = hub.Detail is null ? null : Text(hub.Detail, TextFont, 11, FontWeights.Normal, palette.TextSecondary, width + 6, maxLines: 4).Text;

        var titleHeight = title.Height + (hub.TitleOnPill ? 2 * pillY : 0);
        var total = caption.Height + spacing + titleHeight + (detail is null ? 0 : spacing + detail.Height);
        var y = -total / 2;
        drawing.DrawText(caption, new Point(-caption.MaxTextWidth / 2, y));
        y += caption.Height + spacing;
        if (hub.TitleOnPill)
        {
            var pillWidth = title.Width + (2 * pillX);
            drawing.DrawRoundedRectangle(palette.Accent, null, new Rect(-pillWidth / 2, y, pillWidth, titleHeight), titleHeight / 2, titleHeight / 2);
        }

        drawing.DrawText(title, new Point(-title.MaxTextWidth / 2, y + (hub.TitleOnPill ? pillY : 0)));
        y += titleHeight + spacing;
        if (detail is not null)
        {
            drawing.DrawText(detail, new Point(-detail.MaxTextWidth / 2, y));
        }
    }

    /// <param name="maxWidth">Centers the text in this width and trims it with an ellipsis; 0 for neither.</param>
    private (FormattedText Text, Rect Ink) Text(string text, FontFamily font, double size, FontWeight weight, Brush brush, double maxWidth = 0, int maxLines = 1)
    {
        var key = new TextKey(text, font, size, weight, brush, maxWidth, maxLines);
        if (_texts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0)
        {
            formatted.MaxTextWidth = maxWidth;
            formatted.MaxLineCount = maxLines;
            formatted.TextAlignment = TextAlignment.Center;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }

        var ink = maxWidth > 0 ? Rect.Empty : formatted.BuildGeometry(default).Bounds;
        var entry = (formatted, ink.IsEmpty ? new Rect(0, 0, formatted.Width, formatted.Height) : ink);
        _texts[key] = entry;
        return entry;
    }

    private Geometry BuildWedge(int wedge)
    {
        if (_layout.WedgeCount == 1)
        {
            var ring = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new EllipseGeometry(default, OuterRadius, OuterRadius),
                new EllipseGeometry(default, InnerRadius, InnerRadius));
            ring.Freeze();
            return ring;
        }

        var outline = _layout.Outline(wedge, InnerRadius, OuterRadius, Gap, Corner);
        var core = new StreamGeometry();
        using (var context = core.Open())
        {
            context.BeginFigure(ToPoint(outline.OuterStart), isFilled: true, isClosed: true);
            context.ArcTo(ToPoint(outline.OuterEnd), new Size(outline.OuterRadius, outline.OuterRadius), 0, outline.OuterIsLargeArc, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            context.LineTo(ToPoint(outline.InnerEnd), isStroked: true, isSmoothJoin: false);
            context.ArcTo(ToPoint(outline.InnerStart), new Size(outline.InnerRadius, outline.InnerRadius), 0, outline.InnerIsLargeArc, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: false);
        }

        core.Freeze();
        // Growing the shrunk outline back by the corner radius, with round joins, rounds every corner.
        var grown = core.GetWidenedPathGeometry(new Pen(Brushes.Black, 2 * Corner) { LineJoin = PenLineJoin.Round });
        var rounded = Geometry.Combine(core, grown, GeometryCombineMode.Union, null);
        rounded.Freeze();
        return rounded;
    }

    private readonly record struct TextKey(string Text, FontFamily Font, double Size, FontWeight Weight, Brush Brush, double MaxWidth, int MaxLines);

    /// <summary>
    /// A frosted look without real blur (a layered window can't have one): nearly opaque surfaces, a
    /// soft highlight from the top, a thin rim, and a soft shadow around the edge.
    /// </summary>
    private sealed record Palette(
        Brush Plate,
        Brush Surface,
        Brush SurfaceHover,
        Brush Sheen,
        Brush AccentSheen,
        Pen Rim,
        Brush Hub,
        Brush Accent,
        Brush OnAccent,
        Brush Text,
        Brush TextSecondary,
        Brush TextDisabled,
        Brush Shadow)
    {
        public static Palette For(WheelTheme theme)
        {
            var dark = theme.IsDark;
            return new Palette(
                Plate: Solid(dark ? Color.FromArgb(0xE6, 0x17, 0x17, 0x17) : Color.FromArgb(0xEB, 0xE2, 0xE2, 0xE2)),
                Surface: Solid(dark ? Color.FromArgb(0xF0, 0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF2, 0xFB, 0xFB, 0xFB)),
                SurfaceHover: Solid(dark ? Color.FromArgb(0xF2, 0x3C, 0x3C, 0x3C) : Color.FromArgb(0xF4, 0xEB, 0xEB, 0xEB)),
                Sheen: Vertical(Color.FromArgb(dark ? (byte)0x18 : (byte)0x99, 0xFF, 0xFF, 0xFF)),
                AccentSheen: Vertical(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Rim: Freeze(new Pen(
                    Vertical(dark ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0, 0, 0), dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2B, 0, 0, 0)),
                    1)),
                Hub: Solid(dark ? Color.FromArgb(0xF5, 0x23, 0x23, 0x23) : Color.FromArgb(0xF6, 0xF4, 0xF4, 0xF4)),
                Accent: Solid(theme.Accent),
                OnAccent: Solid(theme.OnAccent),
                Text: Solid(dark ? Colors.White : Color.FromArgb(0xE4, 0, 0, 0)),
                TextSecondary: Solid(dark ? Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x9E, 0, 0, 0)),
                TextDisabled: Solid(dark ? Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x5C, 0, 0, 0)),
                Shadow: EdgeShadow(dark ? (byte)0x70 : (byte)0x38));
        }

        private static SolidColorBrush Solid(Color color) => Freeze(new SolidColorBrush(color));

        /// <summary>Top to bottom across the whole wheel, so the upper wedges catch more of the light.</summary>
        private static LinearGradientBrush Vertical(Color top, Color? bottom = null)
        {
            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, -OuterRadius),
                EndPoint = new Point(0, OuterRadius),
            };
            brush.GradientStops.Add(new GradientStop(top, 0));
            brush.GradientStops.Add(new GradientStop(bottom ?? Color.FromArgb(0, top.R, top.G, top.B), bottom is null ? 0.6 : 1));
            return Freeze(brush);
        }

        /// <summary>Dark just outside the wheel's edge and fading out; clear underneath the wheel.</summary>
        private static RadialGradientBrush EdgeShadow(byte alpha)
        {
            const double radius = OuterRadius + ShadowSize;
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), (OuterRadius - 16) / radius));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 0, 0, 0), (OuterRadius - 2) / radius));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
            return Freeze(brush);
        }
    }
}
