using Sulap.Core.Gestures;

namespace Sulap.Core.Tests;

public sealed class DragEndTrackerTests
{
    private const nint OldImage = 0x100;
    private const nint NewImage = 0x200;

    private readonly DragEndTracker _tracker = new();

    [Fact]
    public void Releasing_the_mouse_ends_the_drag_once()
    {
        _tracker.OnButtonPressed(Ms(0));

        Assert.Equal(DragEndReason.MouseReleased, _tracker.OnButtonReleased(Ms(500)));
        Assert.False(_tracker.IsActive);
        Assert.Null(_tracker.OnButtonReleased(Ms(510)));
    }

    [Fact]
    public void Esc_ends_the_drag()
    {
        _tracker.OnButtonPressed(Ms(0));

        Assert.Equal(DragEndReason.Escape, _tracker.OnEscape(Ms(300)));
        Assert.Null(_tracker.OnEscape(Ms(400)));
    }

    [Fact]
    public void Nothing_ends_before_a_drag_starts()
    {
        Assert.Null(_tracker.OnEscape(Ms(10)));
        Assert.Null(_tracker.OnButtonReleased(Ms(20)));
    }

    [Fact]
    public void A_destroyed_drag_image_waits_for_the_grace_period()
    {
        StartDragWithImage();

        _tracker.OnDragImageDestroyed(OldImage, Ms(100));

        Assert.True(_tracker.IsActive);
        Assert.Equal(Ms(250), _tracker.GraceCheckDue);
        Assert.Null(_tracker.CheckGracePeriod(Ms(200), isMouseButtonDown: false));
        Assert.Equal(Ms(250), _tracker.GraceCheckDue);
    }

    [Fact]
    public void A_replaced_drag_image_keeps_the_drag_going()
    {
        StartDragWithImage();

        _tracker.OnDragImageDestroyed(OldImage, Ms(100));
        var replacedAfter = _tracker.OnDragImageCreated(NewImage, Ms(105));
        _tracker.OnDragImageShown(NewImage, Ms(106));

        Assert.Equal(Ms(5), replacedAfter);
        Assert.Null(_tracker.CheckGracePeriod(Ms(250), isMouseButtonDown: false));
        Assert.True(_tracker.IsActive);
        Assert.Null(_tracker.GraceCheckDue);
    }

    [Fact]
    public void A_replacement_is_reported_once()
    {
        StartDragWithImage();
        _tracker.OnDragImageDestroyed(OldImage, Ms(100));

        Assert.Equal(Ms(8), _tracker.OnDragImageCreated(NewImage, Ms(108)));
        Assert.Null(_tracker.OnDragImageShown(NewImage, Ms(109)));
    }

    [Fact]
    public void No_drag_image_but_a_button_still_down_keeps_the_drag_going()
    {
        StartDragWithImage();

        _tracker.OnDragImageDestroyed(OldImage, Ms(100));

        Assert.Null(_tracker.CheckGracePeriod(Ms(250), isMouseButtonDown: true));
        Assert.True(_tracker.IsActive);
    }

    [Fact]
    public void No_drag_image_and_no_button_down_ends_the_drag()
    {
        StartDragWithImage();

        _tracker.OnDragImageDestroyed(OldImage, Ms(100));

        Assert.Equal(DragEndReason.DragImageGone, _tracker.CheckGracePeriod(Ms(250), isMouseButtonDown: false));
        Assert.False(_tracker.IsActive);
    }

    [Fact]
    public void A_hidden_drag_image_does_not_start_a_grace_period()
    {
        StartDragWithImage();

        _tracker.OnDragImageHidden(OldImage);

        Assert.False(_tracker.HasVisibleDragImage);
        Assert.Null(_tracker.GraceCheckDue);
        Assert.True(_tracker.IsActive);
    }

    [Fact]
    public void A_destroyed_drag_image_after_the_drag_ended_is_ignored()
    {
        StartDragWithImage();
        _tracker.OnButtonReleased(Ms(400));

        _tracker.OnDragImageDestroyed(OldImage, Ms(420));

        Assert.Null(_tracker.GraceCheckDue);
        Assert.Null(_tracker.CheckGracePeriod(Ms(600), isMouseButtonDown: false));
    }

    [Fact]
    public void A_new_press_starts_over()
    {
        StartDragWithImage();
        _tracker.OnDragImageDestroyed(OldImage, Ms(100));

        _tracker.OnButtonPressed(Ms(120));

        Assert.Null(_tracker.GraceCheckDue);
        Assert.Null(_tracker.OnDragImageCreated(NewImage, Ms(130)));
    }

    [Fact]
    public void A_drag_image_starts_a_drag_whose_press_went_unseen()
    {
        _tracker.OnDragImageShown(OldImage, Ms(50));
        Assert.True(_tracker.IsActive);

        _tracker.OnDragImageDestroyed(OldImage, Ms(100));

        Assert.Equal(DragEndReason.DragImageGone, _tracker.CheckGracePeriod(Ms(250), isMouseButtonDown: false));
    }

    [Fact]
    public void The_grace_period_can_be_changed()
    {
        var tracker = new DragEndTracker(TimeSpan.FromMilliseconds(40));
        tracker.OnButtonPressed(Ms(0));

        tracker.OnDragImageDestroyed(OldImage, Ms(100));

        Assert.Equal(Ms(140), tracker.GraceCheckDue);
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private void StartDragWithImage()
    {
        _tracker.OnButtonPressed(Ms(0));
        _tracker.OnDragImageCreated(OldImage, Ms(20));
        _tracker.OnDragImageShown(OldImage, Ms(21));
    }
}
