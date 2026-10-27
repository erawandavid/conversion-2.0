namespace Sulap.Core.Gestures;

public enum DragEndReason
{
    MouseReleased,
    Escape,
    DragImageGone,
}

/// <summary>
/// Decides when a drag is over. Releasing the mouse button and pressing Esc end it right away.
/// The shell's drag image window is a weaker signal: File Explorer destroys it and creates a new
/// one a few milliseconds later when the pointer moves onto another window, so a destroyed drag
/// image only ends the drag if, after a grace period, no drag image is visible and no mouse button
/// is down. Timestamps are any monotonic clock; the caller schedules the grace-period check.
/// </summary>
public sealed class DragEndTracker(TimeSpan gracePeriod)
{
    private readonly HashSet<nint> _visibleDragImages = [];
    private TimeSpan? _dragImageDestroyedAt;

    public DragEndTracker()
        : this(DefaultGracePeriod)
    {
    }

    public static TimeSpan DefaultGracePeriod { get; } = TimeSpan.FromMilliseconds(150);

    public TimeSpan GracePeriod { get; } = gracePeriod;

    /// <summary>True from a mouse press (or a drag image appearing) until the drag ends.</summary>
    public bool IsActive { get; private set; }

    public bool HasVisibleDragImage => _visibleDragImages.Count > 0;

    /// <summary>When the caller should call <see cref="CheckGracePeriod"/>, or null when no check is pending.</summary>
    public TimeSpan? GraceCheckDue { get; private set; }

    public void OnButtonPressed(TimeSpan at)
    {
        IsActive = true;
        GraceCheckDue = null;
        _dragImageDestroyedAt = null;
    }

    public DragEndReason? OnButtonReleased(TimeSpan at) => End(DragEndReason.MouseReleased);

    public DragEndReason? OnEscape(TimeSpan at) => End(DragEndReason.Escape);

    /// <returns>How long after the previous drag image was destroyed this one appeared, when it replaces one.</returns>
    public TimeSpan? OnDragImageCreated(nint handle, TimeSpan at) => TakeReplacementDelay(at);

    /// <returns>How long after the previous drag image was destroyed this one appeared, when it replaces one.</returns>
    public TimeSpan? OnDragImageShown(nint handle, TimeSpan at)
    {
        _visibleDragImages.Add(handle);
        // Covers drags whose mouse press went unseen, for example when the mouse hook isn't running.
        IsActive = true;
        return TakeReplacementDelay(at);
    }

    public void OnDragImageHidden(nint handle) => _visibleDragImages.Remove(handle);

    public void OnDragImageDestroyed(nint handle, TimeSpan at)
    {
        _visibleDragImages.Remove(handle);
        if (IsActive)
        {
            _dragImageDestroyedAt = at;
            GraceCheckDue = at + GracePeriod;
        }
    }

    /// <summary>
    /// Call once <see cref="GraceCheckDue"/> has passed. Ends the drag if no drag image came back
    /// and no mouse button is down.
    /// </summary>
    /// <param name="isMouseButtonDown">Whether a mouse button is down right now, read from the system.</param>
    public DragEndReason? CheckGracePeriod(TimeSpan now, bool isMouseButtonDown)
    {
        if (GraceCheckDue is not { } due || now < due)
        {
            return null;
        }

        GraceCheckDue = null;
        return HasVisibleDragImage || isMouseButtonDown ? null : End(DragEndReason.DragImageGone);
    }

    private TimeSpan? TakeReplacementDelay(TimeSpan at)
    {
        if (!IsActive || _dragImageDestroyedAt is not { } destroyedAt)
        {
            return null;
        }

        _dragImageDestroyedAt = null;
        return at - destroyedAt;
    }

    private DragEndReason? End(DragEndReason reason)
    {
        if (!IsActive)
        {
            return null;
        }

        IsActive = false;
        GraceCheckDue = null;
        _dragImageDestroyedAt = null;
        return reason;
    }
}
