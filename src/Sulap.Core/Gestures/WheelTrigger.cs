using Sulap.Core.Settings;

namespace Sulap.Core.Gestures;

public enum WheelKind
{
    Convert,
    Tools,
    Actions,
}

/// <summary>
/// Turns trigger-key presses during a drag into the wheel to show. The last key pressed wins,
/// and releasing a key changes nothing, so the wheel stays open after the key comes back up.
/// </summary>
public sealed class WheelTrigger(TriggerKeys keys)
{
    public WheelKind? Current { get; private set; }

    /// <summary>The wheel a key opens, or null when it isn't a trigger key.</summary>
    public WheelKind? KindFor(TriggerKey key) =>
        key == TriggerKey.None ? null
        : key == keys.Convert ? WheelKind.Convert
        : key == keys.Tools ? WheelKind.Tools
        : key == keys.Actions ? WheelKind.Actions
        : null;

    /// <summary>Call when a key goes down. Returns true when the wheel to show changed.</summary>
    public bool OnKeyDown(TriggerKey key)
    {
        var kind = KindFor(key);
        if (kind is null || kind == Current)
        {
            return false;
        }

        Current = kind;
        return true;
    }

    /// <summary>Call when the drag ends.</summary>
    public void Reset() => Current = null;
}
