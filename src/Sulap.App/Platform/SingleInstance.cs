using Sulap.App.Interop;
using Sulap.Core;

namespace Sulap.App.Platform;

/// <summary>
/// Keeps one instance per Windows session: a named mutex marks the running instance, and a named
/// event lets a second launch ask it to come forward before exiting.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly string MutexName = $@"Local\{AppIdentity.Name}.Instance";
    private static readonly string ActivateEventName = $@"Local\{AppIdentity.Name}.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly RegisteredWaitHandle _activateWait;

    private SingleInstance(Mutex mutex, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            activateEvent, (_, _) => ActivationRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Raised on a thread-pool thread when another launch asks this instance to show itself.</summary>
    public event Action? ActivationRequested;

    /// <summary>Returns the instance token, or null when another instance is already running.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName));
    }

    public static void ActivateRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var activateEvent))
        {
            return;
        }

        using (activateEvent)
        {
            // This launch came from the user, so it may bring a window to the front; pass that right on.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            activateEvent.Set();
        }
    }

    public void Dispose()
    {
        _activateWait.Unregister(null);
        _activateEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
