using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sulap.App.Platform;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.Tray;

/// <summary>
/// Draws the tray icon in code, at the exact size the notification area wants, in the taskbar's
/// light or dark color. A placeholder until the app gets its own icon design (Phase 6).
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly Color DarkGlyph = Color.FromRgb(0x1C, 0x1C, 0x1C);
    private static readonly Color LightGlyph = Colors.White;

    public static IntPtr CreateIcon(bool dimmed)
    {
        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        var png = RenderPng(size, WindowsTheme.TaskbarUsesLightTheme, dimmed);
        var icon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
        return icon != IntPtr.Zero ? icon : throw new Win32Exception();
    }

    public static byte[] RenderPng(int size, bool lightTaskbar, bool dimmed)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            DrawMark(context, size, lightTaskbar ? DarkGlyph : LightGlyph, dimmed ? 0.4 : 1.0);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Four ring segments around a hub, like a four-wedge wheel. The top segment is solid and the
    /// others are lighter, hinting at a hovered wedge.
    /// </summary>
    private static void DrawMark(DrawingContext context, double size, Color color, double opacity)
    {
        var center = new Point(size / 2, size / 2);
        var outerRadius = size * 0.48;
        var innerRadius = size * 0.25;
        var hubRadius = size * 0.12;
        var gapPixels = Math.Max(1.0, size / 16);
        var gapDegrees = gapPixels * 360 / (Math.PI * (outerRadius + innerRadius));

        var solid = Brush(color, opacity);
        var light = Brush(color, opacity * 0.5);
        for (var i = 0; i < 4; i++)
        {
            var middle = -90 + (i * 90);
            var segment = RingSegment(center, innerRadius, outerRadius, middle - 45 + (gapDegrees / 2), middle + 45 - (gapDegrees / 2));
            context.DrawGeometry(i == 0 ? solid : light, null, segment);
        }

        context.DrawEllipse(solid, null, center, hubRadius, hubRadius);
    }

    private static SolidColorBrush Brush(Color color, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>A ring slice between two angles in degrees, measured clockwise from the positive x axis.</summary>
    private static StreamGeometry RingSegment(Point center, double innerRadius, double outerRadius, double startDegrees, double endDegrees)
    {
        Point At(double radius, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
        }

        var isLargeArc = endDegrees - startDegrees > 180;
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(At(outerRadius, startDegrees), isFilled: true, isClosed: true);
            figure.ArcTo(At(outerRadius, endDegrees), new Size(outerRadius, outerRadius), 0, isLargeArc, SweepDirection.Clockwise, true, false);
            figure.LineTo(At(innerRadius, endDegrees), true, false);
            figure.ArcTo(At(innerRadius, startDegrees), new Size(innerRadius, innerRadius), 0, isLargeArc, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
