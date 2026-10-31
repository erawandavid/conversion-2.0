using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Sulap.Core.Formats;

namespace Sulap.Core.Conversion;

/// <summary>An Office program stopped to show a message box that nobody can see or answer.</summary>
internal sealed class OfficeStoppedException(string message) : Exception(message);

/// <summary>What a job gets while an Office program is running for it.</summary>
internal sealed class OfficeContext(
    OfficeApp app, object application, string copyPath, string outputFolder, Action<ConversionProgress> report, Action touch, Action throwIfStopped)
{
    public OfficeApp App { get; } = app;

    /// <summary>The program's Application object, used late-bound.</summary>
    public dynamic Application { get; } = application;

    /// <summary>The working copy of the source file. The original is never opened.</summary>
    public string CopyPath { get; } = copyPath;

    /// <summary>Where the job writes its results.</summary>
    public string OutputFolder { get; } = outputFolder;

    /// <summary>Shows progress, and counts as a sign of life for the timeout.</summary>
    public void Report(double? fraction, string status)
    {
        touch();
        report(new ConversionProgress(fraction, status));
    }

    /// <summary>Throws when the conversion was cancelled or given up on.</summary>
    public void ThrowIfStopped() => throwIfStopped();

    /// <summary>
    /// Remembers an object a job got from the program, and returns it. The program keeps running for as
    /// long as anything holds a reference to one of its objects, and a garbage collection doesn't let go
    /// of the ones that late-bound calls leave behind. So a job tracks every object it touches, and the
    /// engine releases them all when the job ends. A chain like <c>a.B.C()</c> touches <c>a.B</c> too,
    /// so each step is tracked on its own.
    /// </summary>
    public dynamic Track(dynamic comObject)
    {
        if (comObject is not null && Marshal.IsComObject((object)comObject))
        {
            _tracked.Add((object)comObject);
        }

        return comObject!;
    }

    internal void ReleaseAll()
    {
        foreach (var comObject in _tracked.Reverse())
        {
            try
            {
                Marshal.FinalReleaseComObject(comObject);
            }
            catch (Exception ex) when (ex is InvalidComObjectException or ArgumentException)
            {
                // Already released: the same object can be tracked twice.
            }
        }

        _tracked.Clear();
    }

    private readonly HashSet<object> _tracked = new(ReferenceEqualityComparer.Instance);
}

/// <summary>A finished Office job. Disposing it deletes the working files, so copy the results first.</summary>
internal sealed class OfficeRun<T> : IDisposable
{
    private readonly Action _cleanup;

    public OfficeRun(T result, string outputFolder, Action cleanup)
    {
        Result = result;
        OutputFolder = outputFolder;
        _cleanup = cleanup;
    }

    public T Result { get; }

    public string OutputFolder { get; }

    public void Dispose() => _cleanup();
}

/// <summary>
/// Runs Word, PowerPoint, and Excel invisibly to convert a document, late-bound (no Office
/// references) on a thread of its own.
/// </summary>
/// <remarks>
/// <para>
/// Word and Excel always start a separate, hidden copy of themselves. That copy is found again (by a
/// window caption, or its window handle) so that only it, and the PDF Reflow helper Word starts, can
/// ever be ended. PowerPoint runs once per user: if it's already open, the job shares it, opens its
/// copy of the file without a window, closes only that file, and leaves PowerPoint running.
/// </para>
/// <para>
/// The program opens a copy of the file in a folder of its own, so it never touches the original or
/// the folder the original is in. Macros are switched off. A file with a password is opened with a
/// made-up one, which makes it fail right away instead of asking.
/// </para>
/// <para>
/// Any message box one of these programs shows ends the attempt at once with its text, instead of
/// waiting for the timeout. Stopping a program halfway makes Office note that "the file caused a
/// serious error"; those notes name working copies, and are removed.
/// </para>
/// </remarks>
internal static class OfficeAutomation
{
    /// <summary>Opening with this password fails right away on an encrypted file, and is ignored otherwise.</summary>
    public const string NoPassword = "sulap-no-password";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(400);

    // One job at a time: these programs are heavy, and PowerPoint can only run once.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static string WorkingFolderPrefix => $"{AppIdentity.Name}-office-";

    /// <summary>Runs the job, then waits for the program to close. The result's files live in <see cref="OfficeRun{T}.OutputFolder"/> until it's disposed.</summary>
    /// <param name="prepare">Something to put right when the job is over, such as a setting switched on for it.</param>
    public static async Task<OfficeRun<T>> RunAsync<T>(
        OfficeApp app, string source, TimeSpan timeout, Action<ConversionProgress> report, Func<OfficeContext, T> work,
        CancellationToken cancellationToken, Func<IDisposable>? prepare = null)
    {
        await OneAtATime.WaitAsync(cancellationToken);
        WorkingCopy? copy = null;
        IDisposable? scope = null;
        void Cleanup()
        {
            scope?.Dispose();
            copy?.Dispose();
            OfficeResiliency.ForgetWorkingCopies();
            OneAtATime.Release();
        }

        try
        {
            OfficeResiliency.ForgetWorkingCopies();
            copy = WorkingCopy.Create(source);
            scope = prepare?.Invoke();
            var result = await new Session(app).RunAsync(copy, timeout, report, context => work(context), cancellationToken);
            return new OfficeRun<T>((T)result!, copy.OutputFolder, Cleanup);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>
    /// Puts right what a conversion that was closed halfway left behind: Word's PDF option switched on,
    /// and Office's notes about working copies. Call it once at startup, off the UI thread.
    /// </summary>
    public static void RestoreLeftovers()
    {
        WordPdfReflowOption.RestoreLeftover();
        OfficeResiliency.ForgetWorkingCopies();
    }

    private static string ProgId(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.PowerPoint => "PowerPoint.Application",
        _ => "Excel.Application",
    };

    private static string ProcessName(OfficeApp app) => app switch
    {
        OfficeApp.Word => "WINWORD",
        OfficeApp.PowerPoint => "POWERPNT",
        _ => "EXCEL",
    };

    private static HashSet<int> ProcessIds(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Select(process => process.Id).ToHashSet();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static DateTime? StartTimeOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The process, if it's still the one that started then: an ID is reused once its process ends.</summary>
    private static Process? OpenProcess(int processId, DateTime startedAt)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.StartTime == startedAt)
            {
                var same = process;
                process = null;
                return same;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone.
        }
        finally
        {
            process?.Dispose();
        }

        return null;
    }

    private sealed class Session(OfficeApp app)
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, DateTime> _owned = []; // Process ID → start time. Guarded by _gate.
        private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _sinceProgress = Stopwatch.StartNew(); // Guarded by _gate.
        private (int Id, DateTime StartedAt)? _main; // Guarded by _gate.
        private bool _abandoned; // Guarded by _gate.

        public async Task<object?> RunAsync(
            WorkingCopy copy, TimeSpan timeout, Action<ConversionProgress> report, Func<OfficeContext, object?> work, CancellationToken cancellationToken)
        {
            // An abandoned session still finishes on its own thread, and nobody waits for its outcome.
            _ = _completion.Task.ContinueWith(
                static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            var thread = new Thread(() => Run(copy, report, work)) { IsBackground = true, Name = $"{app} automation" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            while (!_completion.Task.IsCompleted)
            {
                try
                {
                    await Task.WhenAny(_completion.Task, Task.Delay(CheckInterval, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    Abandon();
                    throw;
                }

                if (_completion.Task.IsCompleted)
                {
                    break;
                }

                if (FindDialog() is { } dialog)
                {
                    Abandon();
                    throw new OfficeStoppedException($"{app.DisplayName()} stopped to show a message: \"{dialog}\"");
                }

                TimeSpan idle;
                lock (_gate)
                {
                    idle = _sinceProgress.Elapsed;
                }

                if (idle > timeout)
                {
                    Abandon();
                    var limit = timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0} minutes" : $"{timeout.TotalSeconds:0} seconds";
                    throw new TimeoutException($"{app.DisplayName()} didn't finish within {limit}.");
                }
            }

            return await _completion.Task;
        }

        private void Run(WorkingCopy copy, Action<ConversionProgress> report, Func<OfficeContext, object?> work)
        {
            dynamic? application = null;
            var mayQuit = false;
            object? result = null;
            Exception? failure = null;
            MessageFilter.Register();
            try
            {
                var before = ProcessIds(ProcessName(app));
                var type = Type.GetTypeFromProgID(ProgId(app)) ?? throw new ConversionException($"{app.DisplayName()} isn't installed.");
                application = Activator.CreateInstance(type) ?? throw new ConversionException($"{app.DisplayName()} didn't start.");
                mayQuit = Identify(application, before);
                ThrowIfAbandoned();
                var restore = Configure(application);
                var context = new OfficeContext(app, (object)application, copy.CopyPath, copy.OutputFolder, report, Touch, ThrowIfAbandoned);
                try
                {
                    result = work(context);
                }
                finally
                {
                    context.ReleaseAll();
                    restore();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                var application2 = application;
                if (application2 is not null && mayQuit && OnlyOursOpen(application2, copy.Folder))
                {
                    Try(() => Quit(application2));
                }

                Release(application2);
                application = null;
                if (mayQuit)
                {
                    EndOwned(TimeSpan.FromSeconds(8));
                }

                MessageFilter.Revoke();
                if (failure is null)
                {
                    _completion.TrySetResult(result);
                }
                else
                {
                    _completion.TrySetException(failure);
                }
            }
        }

        /// <summary>Finds the process of the program this session started. Returns whether it may be quit: only an instance this session started.</summary>
        private bool Identify(dynamic application, HashSet<int> before)
        {
            int? processId = null;
            switch (app)
            {
                case OfficeApp.Word:
                    // The window gets a caption that no other window has.
                    var caption = $"{AppIdentity.Name} {Guid.NewGuid():N}";
                    application.Caption = caption;
                    for (var attempt = 0; attempt < 20 && processId is null; attempt++)
                    {
                        processId = NativeWindows.ProcessWithWindow("OpusApp", caption);
                        if (processId is null)
                        {
                            Thread.Sleep(50);
                        }
                    }

                    break;
                case OfficeApp.Excel:
                    processId = NativeWindows.ProcessOfWindow(new IntPtr((int)application.Hwnd));
                    break;
            }

            // Word and Excel always start a new process, so when exactly one started meanwhile, it's this one.
            // PowerPoint starts a process only when none was running; otherwise this job shares the user's.
            var started = ProcessIds(ProcessName(app)).Except(before).ToList();
            if (processId is null && started is [var only])
            {
                processId = only;
            }

            if (processId is { } id && StartTimeOf(id) is { } startedAt)
            {
                lock (_gate)
                {
                    _main = (id, startedAt);
                    _owned[id] = startedAt;
                }
            }

            return processId is { } known && !before.Contains(known);
        }

        /// <summary>Turns off macros and prompts. Returns what puts a shared program's settings back.</summary>
        private Action Configure(dynamic application)
        {
            switch (app)
            {
                case OfficeApp.Word:
                    application.DisplayAlerts = 0;
                    application.AutomationSecurity = 3;
                    break;
                case OfficeApp.Excel:
                    application.AutomationSecurity = 3;
                    application.DisplayAlerts = false;
                    application.ScreenUpdating = false;
                    application.EnableEvents = false;
                    break;
                default:
                    var security = (int)application.AutomationSecurity;
                    var alerts = (int)application.DisplayAlerts;
                    application.AutomationSecurity = 3;
                    application.DisplayAlerts = 1;
                    return () =>
                    {
                        // PowerPoint may be the user's own. Put back what was changed.
                        Try(() => application.AutomationSecurity = security);
                        Try(() => application.DisplayAlerts = alerts);
                    };
            }

            return static () => { };
        }

        /// <summary>True when everything open in the program is a working copy, so quitting can't lose anyone's work.</summary>
        private bool OnlyOursOpen(dynamic application, string workingFolder)
        {
            object? collection = null;
            try
            {
                collection = app switch
                {
                    OfficeApp.Word => application.Documents,
                    OfficeApp.Excel => application.Workbooks,
                    _ => application.Presentations,
                };
                dynamic items = collection!;
                int count = items.Count;
                for (var index = 1; index <= count; index++)
                {
                    dynamic item = items.Item(index);
                    try
                    {
                        string fullName = item.FullName;
                        if (!fullName.StartsWith(workingFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        Release(item);
                    }
                }

                return true;
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return false;
            }
            finally
            {
                Release(collection);
            }
        }

        private void Quit(dynamic application)
        {
            if (app == OfficeApp.Word)
            {
                application.Quit(SaveChanges: 0);
            }
            else
            {
                application.Quit();
            }
        }

        public void Touch()
        {
            lock (_gate)
            {
                _sinceProgress.Restart();
            }
        }

        /// <summary>The text of a message box shown by the program this session started, or by its helpers.</summary>
        private string? FindDialog()
        {
            AddHelpers();
            HashSet<int> ids;
            lock (_gate)
            {
                ids = [.. _owned.Keys];
            }

            return ids.Count == 0 ? null : NativeWindows.FindDialog(ids);
        }

        /// <summary>Gives up on the conversion: the program is ended right away if it's ours, and stops before opening anything if it's still starting.</summary>
        private void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
            }

            EndOwned(TimeSpan.Zero);
        }

        private void ThrowIfAbandoned()
        {
            lock (_gate)
            {
                if (_abandoned)
                {
                    throw new OperationCanceledException("The conversion was stopped.");
                }
            }
        }

        /// <summary>Adds the PDF Reflow helpers our Word has started. Only while it runs: its ID is reused once it's gone.</summary>
        private void AddHelpers()
        {
            if (app != OfficeApp.Word)
            {
                return;
            }

            (int Id, DateTime StartedAt) word;
            lock (_gate)
            {
                if (_main is not { } known)
                {
                    return;
                }

                word = known;
            }

            using (var process = OpenProcess(word.Id, word.StartedAt))
            {
                if (process is null || HasExited(process))
                {
                    return;
                }
            }

            foreach (var (childId, name) in ProcessTree.ChildrenOf(word.Id))
            {
                if (name.Equals("PDFREFLOW.EXE", StringComparison.OrdinalIgnoreCase) && StartTimeOf(childId) is { } startedAt && startedAt >= word.StartedAt)
                {
                    lock (_gate)
                    {
                        _owned.TryAdd(childId, startedAt);
                    }
                }
            }
        }

        /// <summary>Gives the program the grace period to close by itself, then ends whatever of ours is left.</summary>
        private void EndOwned(TimeSpan grace)
        {
            AddHelpers();
            List<KeyValuePair<int, DateTime>> owned;
            int? mainId;
            lock (_gate)
            {
                owned = [.. _owned];
                mainId = _main?.Id;
            }

            var processes = owned.Select(entry => OpenProcess(entry.Key, entry.Value)).OfType<Process>().ToList();
            try
            {
                var waited = Stopwatch.StartNew();
                foreach (var process in processes.OrderBy(process => process.Id == mainId ? 0 : 1))
                {
                    var left = grace - waited.Elapsed;
                    if (left > TimeSpan.Zero)
                    {
                        WaitForExit(process, left);
                    }
                }

                // The helpers first: the program may be waiting on one of them.
                foreach (var process in processes.OrderBy(process => process.Id == mainId ? 1 : 0))
                {
                    Kill(process);
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true;
            }
        }

        private static void WaitForExit(Process process, TimeSpan timeout)
        {
            try
            {
                process.WaitForExit(timeout);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(2)); // So the files it had open are free.
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone.
            }
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Cleanup must not hide the result.
            }
        }

        private static void Release(object? comObject)
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
    }

    /// <summary>A copy of the source in a folder of its own, with an output folder next to it.</summary>
    private sealed class WorkingCopy : IDisposable
    {
        // Office won't open paths much longer than about 218 characters.
        private const int LongestPath = 200;

        private WorkingCopy(string folder, string copyPath, string outputFolder)
        {
            Folder = folder;
            CopyPath = copyPath;
            OutputFolder = outputFolder;
        }

        public string Folder { get; }

        public string CopyPath { get; }

        public string OutputFolder { get; }

        public static WorkingCopy Create(string source)
        {
            var folder = Path.Combine(Path.GetTempPath(), $"{WorkingFolderPrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                var name = Path.GetFileName(source);
                var copy = Path.Combine(folder, folder.Length + name.Length < LongestPath ? name : "Document" + Path.GetExtension(name));
                // A plain byte copy leaves the "downloaded from the internet" mark behind, which would
                // otherwise open the copy in Protected View.
                using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var to = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    from.CopyTo(to);
                }

                var output = Path.Combine(folder, "out");
                Directory.CreateDirectory(output);
                return new WorkingCopy(folder, copy, output);
            }
            catch
            {
                Delete(folder);
                throw;
            }
        }

        public void Dispose() => Delete(Folder);

        private static void Delete(string folder)
        {
            // The program may hold its files for a moment after it closes.
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, recursive: true);
                    }

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100 * attempt);
                }
            }
        }
    }

    /// <summary>
    /// Word's <c>DisableConvertPdfWarning</c> option, switched on only while a PDF is converted. What to
    /// put back is written down first, so a run that's closed halfway is put right by the next one.
    /// </summary>
    internal static class WordPdfReflowOption
    {
        private const string ValueName = "DisableConvertPdfWarning";
        private static readonly object Gate = new();
        private static int _users;
        private static Change? _change;

        private static string NotePath => Path.Combine(AppIdentity.DataDirectory, "word-option.txt");

        /// <summary>Switches the option on until the result is disposed.</summary>
        public static IDisposable Suppress()
        {
            lock (Gate)
            {
                if (_users++ == 0)
                {
                    RestoreLeftoverLocked();
                    _change = InstalledVersion() is { } version ? TurnOn($@"Software\Microsoft\Office\{version}\Word\Options") : null;
                }
            }

            return new Release();
        }

        public static void RestoreLeftover()
        {
            lock (Gate)
            {
                if (_users == 0)
                {
                    RestoreLeftoverLocked();
                }
            }
        }

        /// <summary>"16.0" for Office 2016 to 2024 and Microsoft 365, from the registered Word.Application.</summary>
        private static string? InstalledVersion()
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"Word.Application\CurVer");
            return key?.GetValue(null) is string { } progId && progId.LastIndexOf('.') is var dot and > 0 && int.TryParse(progId[(dot + 1)..], out var major)
                ? $"{major}.0"
                : null;
        }

        private static Change? TurnOn(string keyPath)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                var current = key.GetValue(ValueName);
                if (current is 1 || current is not (null or int))
                {
                    return null; // Already on, or something unexpected that's better left alone.
                }

                var change = new Change(keyPath, current as int?);
                Directory.CreateDirectory(AppIdentity.DataDirectory);
                File.WriteAllLines(NotePath, [keyPath, change.Previous?.ToString(CultureInfo.InvariantCulture) ?? "none"]);
                key.SetValue(ValueName, 1, RegistryValueKind.DWord);
                return change;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                return null; // Word may show its message; the dialog check then ends the attempt.
            }
        }

        private static void RestoreLeftoverLocked()
        {
            try
            {
                if (!File.Exists(NotePath))
                {
                    return;
                }

                var lines = File.ReadAllLines(NotePath);
                if (lines.Length >= 2 && IsOptionsKey(lines[0]))
                {
                    int? previous = int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
                    if (!Put(lines[0], previous))
                    {
                        return; // Keep the note, and try again next time.
                    }
                }

                File.Delete(NotePath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
            }
        }

        private static bool Put(string keyPath, int? previous)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
                if (previous is { } value)
                {
                    key?.SetValue(ValueName, value, RegistryValueKind.DWord);
                }
                else
                {
                    key?.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                return false;
            }
        }

        /// <summary>Only Word's own options key: the note is a file, and a file can be edited.</summary>
        private static bool IsOptionsKey(string path)
        {
            const string prefix = @"Software\Microsoft\Office\";
            const string suffix = @".0\Word\Options";
            return path.Length > prefix.Length + suffix.Length
                && path.StartsWith(prefix, StringComparison.Ordinal)
                && path.EndsWith(suffix, StringComparison.Ordinal)
                && path[prefix.Length..^suffix.Length].All(char.IsAsciiDigit);
        }

        private static void TryDeleteNote()
        {
            try
            {
                File.Delete(NotePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private sealed record Change(string KeyPath, int? Previous);

        private sealed class Release : IDisposable
        {
            private bool _released;

            public void Dispose()
            {
                lock (Gate)
                {
                    if (_released)
                    {
                        return;
                    }

                    _released = true;
                    if (--_users > 0 || _change is not { } change)
                    {
                        return;
                    }

                    _change = null;
                    if (Put(change.KeyPath, change.Previous))
                    {
                        TryDeleteNote();
                    }
                }
            }
        }
    }

    /// <summary>
    /// What Office keeps about a file it was stopped on: a "caused a serious error" mark, and an entry
    /// to offer it for recovery the next time it starts. Those about working copies name files that
    /// are gone, and are removed. Nothing else is touched.
    /// </summary>
    private static class OfficeResiliency
    {
        private static readonly string[] Programs = ["Word", "PowerPoint", "Excel"];

        public static void ForgetWorkingCopies()
        {
            var marker = $@"\{WorkingFolderPrefix}";
            try
            {
                using var office = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office");
                foreach (var version in office?.GetSubKeyNames().Where(name => name.EndsWith(".0", StringComparison.Ordinal)) ?? [])
                {
                    foreach (var program in Programs)
                    {
                        var root = $@"Software\Microsoft\Office\{version}\{program}\Resiliency";
                        ForgetValues($@"{root}\DisabledItems", marker); // One value per file.
                        ForgetValues($@"{root}\StartupItems", marker);
                        ForgetKeys($@"{root}\DocumentRecovery", marker); // One key per document.
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
            {
            }
        }

        private static void ForgetValues(string path, string marker)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (Mentions(key!.GetValue(name), marker))
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
            }
        }

        private static void ForgetKeys(string path, string marker)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                bool ours;
                using (var entry = key!.OpenSubKey(name))
                {
                    ours = entry is { SubKeyCount: 0 } && entry.GetValueNames().Any(value => Mentions(entry.GetValue(value), marker));
                }

                if (ours)
                {
                    key.DeleteSubKey(name, throwOnMissingSubKey: false);
                }
            }
        }

        private static bool Mentions(object? data, string marker) =>
            data is byte[] bytes && Encoding.Unicode.GetString(bytes).Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Retries calls that Office rejects while it's busy, instead of failing with RPC_E_CALL_REJECTED.</summary>
    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int callType, IntPtr callerTask, int tickCount, IntPtr interfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr calleeTask, int tickCount, int rejectType);

        [PreserveSig]
        int MessagePending(IntPtr calleeTask, int tickCount, int pendingType);
    }

    private sealed class MessageFilter : IOleMessageFilter
    {
        private const int ServerCallRetryLater = 2;
        private static readonly int RetryForMilliseconds = 30_000;

        public static void Register() => CoRegisterMessageFilter(new MessageFilter(), out _);

        public static void Revoke() => CoRegisterMessageFilter(null, out _);

        public int HandleInComingCall(int callType, IntPtr callerTask, int tickCount, IntPtr interfaceInfo) => 0;

        // Wait 100 ms and try again while the program is busy (a dialog is open, it's starting up); -1 gives up.
        public int RetryRejectedCall(IntPtr calleeTask, int tickCount, int rejectType) =>
            rejectType == ServerCallRetryLater && tickCount < RetryForMilliseconds ? 100 : -1;

        public int MessagePending(IntPtr calleeTask, int tickCount, int pendingType) => 2;

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);
    }

    /// <summary>Child processes, from a Toolhelp snapshot: it's the one place that lists each process's parent.</summary>
    private static class ProcessTree
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr InvalidHandle = new(-1);

        public static List<(int Id, string Name)> ChildrenOf(int parentId)
        {
            var children = new List<(int Id, string Name)>();
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == InvalidHandle)
            {
                return children;
            }

            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                {
                    if (entry.th32ParentProcessID == (uint)parentId && entry.th32ProcessID != (uint)parentId)
                    {
                        children.Add(((int)entry.th32ProcessID, entry.szExeFile));
                    }
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return children;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }
    }

    private static class NativeWindows
    {
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        /// <summary>The process of a window.</summary>
        public static int? ProcessOfWindow(IntPtr window)
        {
            var thread = GetWindowThreadProcessId(window, out var processId);
            return thread == 0 ? null : (int)processId;
        }

        /// <summary>The process of a top-level window of this class whose title contains this text.</summary>
        public static int? ProcessWithWindow(string className, string titlePart)
        {
            int? found = null;
            EnumWindows(
                (window, _) =>
                {
                    if (ClassName(window) != className || !WindowText(window).Contains(titlePart, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    GetWindowThreadProcessId(window, out var processId);
                    found = (int)processId;
                    return false;
                },
                IntPtr.Zero);
            return found;
        }

        /// <summary>The text of a visible message box belonging to one of these processes.</summary>
        public static string? FindDialog(IReadOnlySet<int> processIds)
        {
            string? found = null;
            EnumWindows(
                (window, _) =>
                {
                    GetWindowThreadProcessId(window, out var processId);
                    if (!processIds.Contains((int)processId) || !IsWindowVisible(window) || !IsDialogClass(ClassName(window)))
                    {
                        return true;
                    }

                    found = DialogText(window);
                    return false;
                },
                IntPtr.Zero);
            return found;
        }

        // #32770 is the standard dialog; NUIDialog is what newer Office uses (for example its password prompt);
        // bosa_sdm_* are the older Office dialogs.
        private static bool IsDialogClass(string className) =>
            className is "#32770" or "NUIDialog" || className.StartsWith("bosa_sdm", StringComparison.Ordinal);

        private static string DialogText(IntPtr dialog)
        {
            var longest = string.Empty;
            EnumChildWindows(
                dialog,
                (child, _) =>
                {
                    var text = WindowText(child);
                    if (text.Length > longest.Length)
                    {
                        longest = text;
                    }

                    return true;
                },
                IntPtr.Zero);
            return longest.Length > 0 ? longest : WindowText(dialog);
        }

        private static string ClassName(IntPtr window)
        {
            var buffer = new char[64];
            var length = GetClassName(window, buffer, buffer.Length);
            return new string(buffer, 0, Math.Max(0, length));
        }

        private static string WindowText(IntPtr window)
        {
            var buffer = new char[512];
            var length = GetWindowText(window, buffer, buffer.Length);
            return new string(buffer, 0, Math.Max(0, length));
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, [Out] char[] className, int maxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, [Out] char[] text, int maxCount);
    }
}
