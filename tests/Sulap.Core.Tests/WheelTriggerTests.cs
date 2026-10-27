using Sulap.Core.Gestures;
using Sulap.Core.Settings;

namespace Sulap.Core.Tests;

public sealed class WheelTriggerTests
{
    private readonly WheelTrigger _trigger = new(new TriggerKeys());

    [Fact]
    public void Nothing_is_open_before_a_trigger_key()
    {
        Assert.Null(_trigger.Current);
    }

    [Theory]
    [InlineData(TriggerKey.Shift, WheelKind.Convert)]
    [InlineData(TriggerKey.Ctrl, WheelKind.Tools)]
    [InlineData(TriggerKey.Alt, WheelKind.Actions)]
    public void Default_keys_open_their_wheels(TriggerKey key, WheelKind expected)
    {
        Assert.True(_trigger.OnKeyDown(key));
        Assert.Equal(expected, _trigger.Current);
    }

    [Fact]
    public void The_last_key_pressed_wins()
    {
        _trigger.OnKeyDown(TriggerKey.Shift);
        _trigger.OnKeyDown(TriggerKey.Ctrl);
        Assert.Equal(WheelKind.Tools, _trigger.Current);

        Assert.True(_trigger.OnKeyDown(TriggerKey.Shift));
        Assert.Equal(WheelKind.Convert, _trigger.Current);
    }

    [Fact]
    public void Pressing_the_same_key_again_changes_nothing()
    {
        _trigger.OnKeyDown(TriggerKey.Shift);

        Assert.False(_trigger.OnKeyDown(TriggerKey.Shift));
        Assert.Equal(WheelKind.Convert, _trigger.Current);
    }

    [Fact]
    public void Keys_follow_the_settings()
    {
        var trigger = new WheelTrigger(new TriggerKeys { Convert = TriggerKey.Alt, Actions = TriggerKey.None });

        Assert.Equal(WheelKind.Convert, trigger.KindFor(TriggerKey.Alt));
        Assert.Equal(WheelKind.Tools, trigger.KindFor(TriggerKey.Ctrl));
        Assert.Null(trigger.KindFor(TriggerKey.Shift));
        Assert.Null(trigger.KindFor(TriggerKey.None));
    }

    [Fact]
    public void Reset_closes_the_wheel()
    {
        _trigger.OnKeyDown(TriggerKey.Ctrl);

        _trigger.Reset();

        Assert.Null(_trigger.Current);
        Assert.True(_trigger.OnKeyDown(TriggerKey.Ctrl));
    }
}
