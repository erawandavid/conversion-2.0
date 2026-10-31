using System.Diagnostics;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.DragDetection;

internal enum PolledKey
{
    Shift,
    Ctrl,
    Alt,
    Esc,
    LeftButton,
    RightButton,
}

internal readonly record struct KeyChange(PolledKey Key, bool IsDown, long Timestamp);

/// <summary>
/// Polls GetAsyncKeyState about 60 times a second on its own thread and reports changes. During a
/// drag, the dragging app's modal loop owns the input, so polling is how another app sees keys.
/// </summary>
internal sealed class KeyStatePoller : IDisposable
{
    private static readonly (PolledKey Key, int VirtualKey)[] Keys =
    [
        (PolledKey.Shift, VK_SHIFT),
        (PolledKey.Ctrl, VK_CONTROL),
        (PolledKey.Alt, VK_MENU),
        (PolledKey.Esc, VK_ESCAPE),
        (PolledKey.LeftButton, VK_LBUTTON),
        (PolledKey.RightButton, VK_RBUTTON),
    ];

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(initialState: true);
    private readonly bool _onlyWhileButtonDown;
    private volatile bool _stopping;
    private long _polls;
    private long _longestGapTicks;
    private long _firstPoll;
    private long _lastPoll;

    /// <param name="onlyWhileButtonDown">
    /// Sleep while no mouse button is down, until <see cref="Wake"/>. Keys only matter during a drag,
    /// and polling all day would wake the CPU about 60 times a second for nothing.
    /// </param>
    public KeyStatePoller(bool onlyWhileButtonDown = false)
    {
        _onlyWhileButtonDown = onlyWhileButtonDown;
        _thread = new Thread(Run) { Name = "Key polling", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Raised on the polling thread.</summary>
    public event Action<KeyChange>? Changed;

    public long Polls => Volatile.Read(ref _polls);

    public TimeSpan AverageInterval
    {
        get
        {
            var polls = Polls;
            return polls < 2
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((Volatile.Read(ref _lastPoll) - Volatile.Read(ref _firstPoll)) / (double)Stopwatch.Frequency / (polls - 1));
        }
    }

    public TimeSpan LongestInterval => TimeSpan.FromSeconds(Volatile.Read(ref _longestGapTicks) / (double)Stopwatch.Frequency);

    /// <summary>Starts polling again after a pause. Cheap enough to call from a mouse hook.</summary>
    public void Wake() => _wake.Set();

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsAnyButtonDown() => IsDown(VK_LBUTTON) || IsDown(VK_RBUTTON);

    private void Run()
    {
        var down = Keys.Select(key => IsDown(key.VirtualKey)).ToArray();
        while (!_stopping)
        {
            var now = Stopwatch.GetTimestamp();
            RecordPoll(now);
            for (var i = 0; i < Keys.Length; i++)
            {
                var isDown = IsDown(Keys[i].VirtualKey);
                if (isDown != down[i])
                {
                    down[i] = isDown;
                    try
                    {
                        Changed?.Invoke(new KeyChange(Keys[i].Key, isDown, now));
                    }
                    catch (Exception)
                    {
                        // A failing listener must not stop the polling.
                    }
                }
            }

            Thread.Sleep(15);
            if (_onlyWhileButtonDown && !IsAnyButtonDown())
            {
                _wake.Reset();
                // Checked again after the reset: a press in between either shows here or sets the event.
                if (!IsAnyButtonDown())
                {
                    _wake.Wait();
                }

                // Start from the keys as they are now: what changed while asleep belongs to no drag.
                for (var i = 0; i < Keys.Length; i++)
                {
                    down[i] = IsDown(Keys[i].VirtualKey);
                }
            }
        }
    }

    private void RecordPoll(long now)
    {
        // Only the polling thread writes these; other threads just read them.
        if (_polls == 0)
        {
            Volatile.Write(ref _firstPoll, now);
        }
        else if (now - _lastPoll > _longestGapTicks)
        {
            Volatile.Write(ref _longestGapTicks, now - _lastPoll);
        }

        Volatile.Write(ref _lastPoll, now);
        Volatile.Write(ref _polls, _polls + 1);
    }
}
