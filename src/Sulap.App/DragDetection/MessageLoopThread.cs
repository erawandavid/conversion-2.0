using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.DragDetection;

/// <summary>
/// A background thread running a Win32 message loop. Hooks call back on the thread that installed
/// them, so each hook gets a thread of its own, away from the UI thread's layout and rendering.
/// </summary>
internal sealed class MessageLoopThread : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private Exception? _installError;

    /// <param name="install">Runs on the new thread before the loop starts. Throw to report a failure.</param>
    /// <param name="uninstall">Runs on the thread after the loop ends.</param>
    public MessageLoopThread(string name, ThreadPriority priority, Action install, Action uninstall)
    {
        _thread = new Thread(() => Run(install, uninstall)) { Name = name, IsBackground = true, Priority = priority };
        _thread.Start();
        _ready.Wait();
        if (_installError is not null)
        {
            _thread.Join();
            _ready.Dispose();
            throw _installError;
        }
    }

    public void Dispose()
    {
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Run(Action install, Action uninstall)
    {
        _threadId = GetCurrentThreadId();
        // Create the thread's message queue now, so a WM_QUIT posted early isn't lost.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        try
        {
            install();
        }
        catch (Exception ex)
        {
            _installError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(in message);
            DispatchMessage(in message);
        }

        uninstall();
    }
}
