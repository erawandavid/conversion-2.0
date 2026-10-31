#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sulap.App.Jobs;
using Sulap.App.Views;
using Sulap.App.Wheel;
using Sulap.Core.Conversion;
using Sulap.Core.Formats;
using Sulap.Core.Settings;
using static Sulap.App.Interop.NativeMethods;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Sulap.App.Diagnostics;

/// <summary>
/// Drops files on the wheel through OLE the way File Explorer does, without touching the real pointer:
/// it calls the drop target that WPF registered for the wheel window. Then it waits for the
/// conversions and checks what they saved. Writes <c>drop-test.log</c> to the folder.
/// </summary>
internal static class DropTest
{
    private const int MK_LBUTTON = 0x1;
    private const int DROPEFFECT_NONE = 0;
    private const int DROPEFFECT_COPY = 1;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DragEnterMethod(IntPtr self, IntPtr data, int keyState, long point, ref int effect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DragOverMethod(IntPtr self, int keyState, long point, ref int effect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DragLeaveMethod(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DropMethod(IntPtr self, IntPtr data, int keyState, long point, ref int effect);

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new List<string>();
        var failed = false;
        void Check(bool passed, string what)
        {
            log.Add($"{(passed ? "PASS" : "FAIL")}  {what}");
            failed |= !passed;
        }

        try
        {
            var files = Path.Combine(folder, "files");
            if (Directory.Exists(files))
            {
                Directory.Delete(files, recursive: true);
            }

            Directory.CreateDirectory(files);
            var first = Path.Combine(files, "Drop test 1.png");
            var second = Path.Combine(files, "Drop test 2.png");
            WriteSamplePng(first, Colors.SteelBlue);
            WriteSamplePng(second, Colors.IndianRed);
            var original = Fingerprint(first);

            var engine = new ConversionEngine();
            var jobs = new ConversionJobs(engine);
            var results = new List<ConversionResult?>();
            jobs.Finished += (_, result) => results.Add(result);
            using var wheel = new WheelController(new TriggerKeys(), engine, jobs, watchMouseButtons: false);
            var center = PrimaryWorkAreaCenter();

            wheel.ShowSensorAt(center, IntPtr.Zero, nudge: false);
            var text = new DataObject(DataFormats.UnicodeText, "Some dragged text");
            var effect = new DropTarget(wheel.WindowHandle).DragEnter(text, center);
            Check(effect == DROPEFFECT_NONE && wheel.State == WheelState.Hidden, "dragged text: no wheel, and the sensor steps aside");

            // Files Sulap can't convert: a notice, which refuses the drop and goes away with the pointer.
            wheel.ShowSensorAt(center, IntPtr.Zero, nudge: false);
            var zip = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(files, "Archive.zip") });
            var zipTarget = new DropTarget(wheel.WindowHandle);
            effect = zipTarget.DragEnter(zip, center);
            Check(wheel.State == WheelState.Open && wheel.IsNotice && effect == DROPEFFECT_NONE, "a ZIP file: the wheel shows a notice and refuses the drop");
            Check(zipTarget.DragOver(wheel.HubScreenPoint()) == DROPEFFECT_NONE, "a ZIP file: moving over the notice still refuses the drop");
            zipTarget.DragLeave();
            Check(wheel.State is WheelState.Closing or WheelState.Hidden, "a ZIP file: the notice closes when the pointer leaves it");
            wheel.Stop();

            // Office documents get their own wheels, whether or not this PC has the program.
            foreach (var (name, expected) in new[] { ("Deck.pptx", "PDF, JPG, PNG"), ("Budget.xlsx", "PDF, CSV"), ("Letter.docx", "PDF, TXT") })
            {
                wheel.ShowSensorAt(center, IntPtr.Zero, nudge: false);
                new DropTarget(wheel.WindowHandle).DragEnter(new DataObject(DataFormats.FileDrop, new[] { Path.Combine(files, name) }), wheel.HubScreenPoint());
                Check(wheel.State == WheelState.Open && !wheel.IsNotice && string.Join(", ", wheel.Options.Select(option => option.Label)) == expected, $"{name}: the wheel offers {expected}");
                wheel.Stop();
            }

            DropOn(wheel, [first], FileFormat.Jpeg, center, Check);
            DropOn(wheel, [first, second], FileFormat.Pdf, center, Check);

            Check(WaitFor(() => results.Count == 2), "both conversions finished within a minute");
            Check(results.Count == 2 && results.All(result => result?.Succeeded == true), "both conversions succeeded");
            Check(StartsWith(Path.Combine(files, "Drop test 1.jpg"), [0xFF, 0xD8, 0xFF]), "Drop test 1.jpg was saved, and it's a JPEG");
            Check(StartsWith(Path.Combine(files, "Drop test 1.pdf"), "%PDF"u8.ToArray()), "Drop test 1.pdf was saved, and it's a PDF");
            Check(Fingerprint(first) == original, "the dropped file is unchanged");
            Check(!Directory.EnumerateFileSystemEntries(files, "~*").Any(), "no temporary files are left");
            Check(Directory.GetFiles(files).Length == 4, "nothing else was written");

            foreach (var toast in Application.Current.Windows.OfType<ToastWindow>().ToList())
            {
                toast.Close();
            }
        }
        catch (Exception ex)
        {
            log.Add($"ERROR  {ex}");
            failed = true;
        }

        File.WriteAllLines(Path.Combine(folder, "drop-test.log"), log);
        return failed ? 1 : 0;
    }

    /// <summary>Opens the wheel for these files with the pointer on the hub, moves to a wedge, and drops.</summary>
    private static void DropOn(WheelController wheel, string[] paths, FileFormat target, POINT start, Action<bool, string> check)
    {
        var name = paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} files";
        var data = new DataObject(DataFormats.FileDrop, paths);
        wheel.ShowSensorAt(start, IntPtr.Zero, nudge: false);
        var drop = new DropTarget(wheel.WindowHandle);

        var hub = wheel.HubScreenPoint();
        var entered = drop.DragEnter(data, hub);
        check(wheel.State == WheelState.Open && entered == DROPEFFECT_NONE, $"{name}: the wheel opens, and its hub takes no drop");

        var wedge = wheel.Options.ToList().FindIndex(option => option.Target == target);
        check(wedge >= 0, $"{name}: the wheel offers {target.Label()}");
        var point = wheel.WedgeScreenPoint(wedge);
        check(drop.DragOver(point) == DROPEFFECT_COPY, $"{name}: {target.Label()} accepts the drop as a copy");

        drop.DragLeave();
        check(wheel.State == WheelState.Open, $"{name}: the wheel stays open when the pointer leaves it");
        drop.DragEnter(data, point);
        check(drop.Drop(data, point) == DROPEFFECT_COPY, $"{name}: dropped on {target.Label()}");
        check(wheel.State is WheelState.Closing or WheelState.Hidden, $"{name}: the wheel closes after the drop");
    }

    private static POINT PrimaryWorkAreaCenter()
    {
        // (0, 0) is always on the primary monitor.
        var monitor = MonitorFromPoint(default, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        return new POINT { X = (info.rcWork.Left + info.rcWork.Right) / 2, Y = (info.rcWork.Top + info.rcWork.Bottom) / 2 };
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow > deadline)
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return condition();
    }

    private static void WriteSamplePng(string path, Color color)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 320, 200));
            context.DrawEllipse(Brushes.White, null, new Point(160, 100), 60, 60);
        }

        var bitmap = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Fingerprint(string path) =>
        $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))} {File.GetLastWriteTimeUtc(path):O}";

    private static bool StartsWith(string path, byte[] signature)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var head = new byte[signature.Length];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == head.Length && head.AsSpan().SequenceEqual(signature);
    }

    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    /// <summary>
    /// The IDropTarget that WPF registered for a window (RegisterDragDrop keeps it in the window's
    /// "OleDropTargetInterface" property), called through its vtable as OLE's DoDragDrop would.
    /// Points are in physical screen pixels, packed into a POINTL.
    /// </summary>
    private sealed class DropTarget
    {
        private readonly IntPtr _target;

        public DropTarget(IntPtr window)
        {
            _target = GetProp(window, "OleDropTargetInterface");
            if (_target == IntPtr.Zero)
            {
                throw new InvalidOperationException("The wheel window has no registered drop target.");
            }
        }

        public int DragEnter(DataObject data, POINT point)
        {
            var effect = DROPEFFECT_COPY;
            WithComData(data, pointer => Marshal.ThrowExceptionForHR(Method<DragEnterMethod>(3)(_target, pointer, MK_LBUTTON, Pack(point), ref effect)));
            return effect;
        }

        public int DragOver(POINT point)
        {
            var effect = DROPEFFECT_COPY;
            Marshal.ThrowExceptionForHR(Method<DragOverMethod>(4)(_target, MK_LBUTTON, Pack(point), ref effect));
            return effect;
        }

        public void DragLeave() => Marshal.ThrowExceptionForHR(Method<DragLeaveMethod>(5)(_target));

        public int Drop(DataObject data, POINT point)
        {
            var effect = DROPEFFECT_COPY;
            WithComData(data, pointer => Marshal.ThrowExceptionForHR(Method<DropMethod>(6)(_target, pointer, MK_LBUTTON, Pack(point), ref effect)));
            return effect;
        }

        private static long Pack(POINT point) => ((long)point.Y << 32) | (uint)point.X;

        private static void WithComData(DataObject data, Action<IntPtr> use)
        {
            var pointer = Marshal.GetComInterfaceForObject<DataObject, ComDataObject>(data);
            try
            {
                use(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        /// <summary>A method of the interface by its vtable slot: 0 to 2 are IUnknown's, then DragEnter, DragOver, DragLeave, Drop.</summary>
        private T Method<T>(int slot)
            where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(_target), slot * IntPtr.Size));
    }
}
#endif
