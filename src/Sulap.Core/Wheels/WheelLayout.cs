namespace Sulap.Core.Wheels;

public enum WheelHitKind
{
    /// <summary>Beyond the wheel's edge.</summary>
    Outside,

    /// <summary>The hub, or the space between the hub and the wedges.</summary>
    Hub,

    Wedge,
}

public readonly record struct WheelHit(WheelHitKind Kind, int Wedge = -1)
{
    public static WheelHit Outside { get; } = new(WheelHitKind.Outside);

    public static WheelHit Hub { get; } = new(WheelHitKind.Hub);
}

/// <summary>A point relative to the wheel's center, with y pointing down like screen coordinates.</summary>
public readonly record struct WheelPoint(double X, double Y);

/// <summary>
/// The four corners of a wedge shrunk by <c>inset</c> on every side. Filling this shape and stroking
/// it with a pen of width 2 × inset and round joins gives a wedge of the full size with rounded corners.
/// </summary>
public readonly record struct WedgeOutline(
    WheelPoint OuterStart, WheelPoint OuterEnd, WheelPoint InnerEnd, WheelPoint InnerStart,
    double OuterRadius, double InnerRadius, bool OuterIsLargeArc, bool InnerIsLargeArc);

/// <summary>
/// Geometry of a wheel. Wedge 0 is centered at 12 o'clock and the others follow clockwise, so four
/// wedges sit at the top, right, bottom, and left. Angles are degrees clockwise from 12 o'clock.
/// </summary>
/// <param name="InnerRadius">Where the wedges start; closer to the center is the hub.</param>
/// <param name="OuterRadius">Where the wheel ends; farther out is outside.</param>
public sealed record WheelLayout(int WedgeCount, double InnerRadius, double OuterRadius)
{
    public double SweepDegrees => WedgeCount == 0 ? 0 : 360d / WedgeCount;

    public double CenterAngle(int wedge) => wedge * SweepDegrees;

    public static double AngleOf(double x, double y)
    {
        var degrees = Math.Atan2(x, -y) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    public static WheelPoint PointAt(double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new WheelPoint(radius * Math.Sin(radians), -radius * Math.Cos(radians));
    }

    /// <summary>Where the point lands. The gaps drawn between wedges count as the nearest wedge.</summary>
    public WheelHit HitTest(double x, double y)
    {
        var distance = Math.Sqrt((x * x) + (y * y));
        if (distance > OuterRadius)
        {
            return WheelHit.Outside;
        }

        if (distance < InnerRadius || WedgeCount == 0)
        {
            return WheelHit.Hub;
        }

        var wedge = (int)Math.Floor((AngleOf(x, y) + (SweepDegrees / 2)) / SweepDegrees) % WedgeCount;
        return new WheelHit(WheelHitKind.Wedge, wedge);
    }

    /// <summary>The middle of a wedge at a given radius, for its label.</summary>
    public WheelPoint LabelPoint(int wedge, double radius) => PointAt(radius, CenterAngle(wedge));

    /// <summary>
    /// The outline of a wedge between two radii, with <paramref name="gap"/> pixels between
    /// neighbors and everything shrunk by <paramref name="inset"/> (the corner radius).
    /// Only for two or more wedges; a single wedge is a full ring.
    /// </summary>
    public WedgeOutline Outline(int wedge, double innerRadius, double outerRadius, double gap, double inset)
    {
        if (WedgeCount < 2)
        {
            throw new InvalidOperationException("A wheel with fewer than two wedges has no wedge edges.");
        }

        var start = CenterAngle(wedge) - (SweepDegrees / 2);
        var end = start + SweepDegrees;
        var offset = (gap / 2) + inset; // How far the shrunk edges sit from the true radial edge.
        var outer = outerRadius - inset;
        var inner = innerRadius + inset;

        return new WedgeOutline(
            OnEdge(outer, start, offset),
            OnEdge(outer, end, -offset),
            OnEdge(inner, end, -offset),
            OnEdge(inner, start, offset),
            outer,
            inner,
            SpanDegrees(outer, offset) > 180,
            SpanDegrees(inner, offset) > 180);
    }

    /// <summary>
    /// The point at <paramref name="radius"/> from the center on the line parallel to the radial
    /// edge at <paramref name="angle"/>, moved sideways by <paramref name="offset"/> (positive = clockwise).
    /// </summary>
    private static WheelPoint OnEdge(double radius, double angle, double offset)
    {
        var radians = angle * Math.PI / 180;
        var along = Math.Sqrt(Math.Max(0, (radius * radius) - (offset * offset)));
        var (sin, cos) = Math.SinCos(radians);
        // Radial direction (sin, -cos); clockwise tangent (cos, sin).
        return new WheelPoint((along * sin) + (offset * cos), (-along * cos) + (offset * sin));
    }

    private double SpanDegrees(double radius, double offset) =>
        SweepDegrees - (2 * Math.Asin(Math.Min(1, offset / radius)) * 180 / Math.PI);
}
