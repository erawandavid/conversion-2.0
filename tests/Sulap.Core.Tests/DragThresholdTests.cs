using Sulap.Core.Gestures;

namespace Sulap.Core.Tests;

public sealed class DragThresholdTests
{
    private readonly DragThreshold _threshold = new(4, 4);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(-4, 4)]
    [InlineData(3, -3)]
    public void Small_moves_stay_a_click(int deltaX, int deltaY)
    {
        Assert.False(_threshold.IsExceeded(deltaX, deltaY));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(0, -5)]
    [InlineData(-12, 2)]
    public void Moving_past_either_side_starts_a_drag(int deltaX, int deltaY)
    {
        Assert.True(_threshold.IsExceeded(deltaX, deltaY));
    }
}
