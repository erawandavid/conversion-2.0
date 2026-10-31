using Sulap.Core.Wheels;

namespace Sulap.Core.Tests;

public sealed class WheelLayoutTests
{
    private readonly WheelLayout _four = new(4, InnerRadius: 60, OuterRadius: 140);
    private readonly WheelLayout _eight = new(8, InnerRadius: 60, OuterRadius: 140);

    [Theory]
    [InlineData(0, -100, 0)] // top
    [InlineData(100, 0, 1)] // right
    [InlineData(0, 100, 2)] // bottom
    [InlineData(-100, 0, 3)] // left
    [InlineData(60, -70, 0)] // up and a little right is still the top wedge
    public void Four_wedges_sit_top_right_bottom_left(double x, double y, int expected)
    {
        Assert.Equal(new WheelHit(WheelHitKind.Wedge, expected), _four.HitTest(x, y));
    }

    [Theory]
    [InlineData(22.4, 0)]
    [InlineData(22.6, 1)]
    [InlineData(90, 2)]
    [InlineData(337.4, 7)]
    [InlineData(337.6, 0)]
    public void Eight_wedges_split_at_22_and_a_half_degrees(double angle, int expected)
    {
        var point = WheelLayout.PointAt(100, angle);

        Assert.Equal(expected, _eight.HitTest(point.X, point.Y).Wedge);
    }

    [Fact]
    public void The_center_is_the_hub_and_beyond_the_rim_is_outside()
    {
        Assert.Equal(WheelHit.Hub, _four.HitTest(0, 0));
        Assert.Equal(WheelHit.Hub, _four.HitTest(30, -40));
        Assert.Equal(WheelHit.Outside, _four.HitTest(0, -141));
        Assert.Equal(WheelHit.Outside, _four.HitTest(100, 100));
    }

    [Fact]
    public void A_wheel_without_wedges_is_all_hub()
    {
        var empty = new WheelLayout(0, 60, 140);

        Assert.Equal(WheelHit.Hub, empty.HitTest(0, -100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(200)]
    [InlineData(359)]
    public void Angles_and_points_round_trip(double angle)
    {
        var point = WheelLayout.PointAt(50, angle);

        Assert.Equal(angle, WheelLayout.AngleOf(point.X, point.Y), precision: 6);
    }

    [Fact]
    public void Wedge_outline_corners_sit_on_the_shrunk_circles_and_clear_the_gap()
    {
        const double gap = 4;
        const double inset = 6;
        var outline = _four.Outline(1, innerRadius: 64, outerRadius: 132, gap, inset);

        Assert.Equal(126, outline.OuterRadius);
        Assert.Equal(70, outline.InnerRadius);
        Assert.Equal(126, Length(outline.OuterStart), precision: 6);
        Assert.Equal(126, Length(outline.OuterEnd), precision: 6);
        Assert.Equal(70, Length(outline.InnerEnd), precision: 6);
        Assert.Equal(70, Length(outline.InnerStart), precision: 6);

        // Wedge 1 (right) spans 45°–135°. Its start edge is the line at 45°; the shrunk corner keeps
        // half the gap plus the inset away from it.
        var edge = WheelLayout.PointAt(1, 45);
        var distanceFromEdge = Math.Abs((outline.OuterStart.X * edge.Y) - (outline.OuterStart.Y * edge.X));
        Assert.Equal((gap / 2) + inset, distanceFromEdge, precision: 6);
        Assert.False(outline.OuterIsLargeArc);
    }

    [Fact]
    public void Wedge_outline_corners_stay_inside_their_wedge()
    {
        for (var wedge = 0; wedge < 8; wedge++)
        {
            var outline = _eight.Outline(wedge, 64, 132, gap: 4, inset: 6);

            foreach (var corner in new[] { outline.OuterStart, outline.OuterEnd, outline.InnerEnd, outline.InnerStart })
            {
                Assert.Equal(wedge, _eight.HitTest(corner.X, corner.Y).Wedge);
            }
        }
    }

    private static double Length(WheelPoint point) => Math.Sqrt((point.X * point.X) + (point.Y * point.Y));
}
