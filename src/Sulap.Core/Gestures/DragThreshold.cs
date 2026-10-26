namespace Sulap.Core.Gestures;

/// <summary>
/// How far the pointer must move from the press point before a press becomes a drag. Windows
/// defines it as SM_CXDRAG/SM_CYDRAG pixels on either side of the press point.
/// </summary>
public readonly record struct DragThreshold(int Horizontal, int Vertical)
{
    public bool IsExceeded(int deltaX, int deltaY) =>
        Math.Abs(deltaX) > Horizontal || Math.Abs(deltaY) > Vertical;
}
