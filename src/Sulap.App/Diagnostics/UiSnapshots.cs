#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sulap.App.Tray;
using Sulap.App.Views;
using Sulap.App.Wheel;
using Sulap.Core;
using Sulap.Core.Formats;
using Sulap.Core.Settings;

namespace Sulap.App.Diagnostics;

/// <summary>Renders UI to PNG files with RenderTargetBitmap.</summary>
internal static class UiSnapshots
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48];

    private static readonly Color LightSurface = Color.FromRgb(0xF3, 0xF3, 0xF3);
    private static readonly Color DarkSurface = Color.FromRgb(0x20, 0x20, 0x20);

    public static int RenderAll(string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            RenderTrayIconSheet(Path.Combine(folder, "tray-icons.png"));
            foreach (var dark in new[] { false, true })
            {
                var theme = dark ? ThemeMode.Dark : ThemeMode.Light;
                var suffix = dark ? "dark" : "light";
                RenderWindow(new AboutWindow { ThemeMode = theme }, dark, Path.Combine(folder, $"about-{suffix}.png"));
                RenderWindow(
                    new SettingsWindow(SettingsStore.ForCurrentUser().FilePath) { ThemeMode = theme },
                    dark,
                    Path.Combine(folder, $"settings-{suffix}.png"));
                RenderWheelSheet(dark, Path.Combine(folder, $"wheels-{suffix}.png"));
                RenderToasts(theme, dark, folder, suffix);
            }

            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "render-error.txt"), ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// The wheel in the states that matter, over a stand-in for File Explorer. Row 1: a PDF with DOCX under
    /// the pointer; a JPG with PNG under it; a JPG with the disabled HEIC under it. Row 2: three images
    /// with PDF under the pointer; a long file name; a JPG, a PNG and a PDF, which share a single option.
    /// Row 3: a presentation with JPG under the pointer; a workbook with CSV under it; a presentation on a
    /// PC without PowerPoint. Row 4: a ZIP file and a text file, which get a notice; a JPG and a Word
    /// document, which share PDF.
    /// </summary>
    private static void RenderWheelSheet(bool dark, string path)
    {
        var withoutHeic = new FormatRegistry(EngineCapabilities.All with
        {
            MagickWrites = EngineCapabilities.All.MagickWrites.Where(format => format != FileFormat.Heic).ToHashSet(),
            WindowsHeifEncoder = false,
        });
        var everything = new FormatRegistry(EngineCapabilities.All);
        var withoutPowerPoint = new FormatRegistry(EngineCapabilities.All with { PowerPoint = false });
        (string[] Files, FormatRegistry Registry, FileFormat? Hovered)[] cells =
        [
            (["Report.pdf"], everything, FileFormat.Docx),
            (["Holiday.jpg"], withoutHeic, FileFormat.Png),
            (["Holiday.jpg"], withoutHeic, FileFormat.Heic),
            (["Scan 1.png", "Scan 2.png", "Scan 3.png"], everything, FileFormat.Pdf),
            (["Holiday at the beach with the whole family.jpg"], withoutHeic, null),
            (["Holiday.jpg", "Logo.png", "Report.pdf"], everything, null),
            (["Quarterly results.pptx"], everything, FileFormat.Jpeg),
            (["Budget 2026.xlsx"], everything, FileFormat.Csv),
            (["Quarterly results.pptx"], withoutPowerPoint, FileFormat.Pdf),
            (["Archive.zip"], everything, null),
            (["Notes.txt"], everything, null),
            (["Holiday.jpg", "Letter.docx"], everything, FileFormat.Pdf),
        ];

        const int columns = 3;
        var size = (int)WheelView.WindowSize;
        var sheet = new DrawingVisual();
        using (var context = sheet.RenderOpen())
        {
            for (var i = 0; i < cells.Length; i++)
            {
                var (files, registry, hovered) = cells[i];
                var content = WheelController.Describe(files, registry) ?? throw new InvalidOperationException($"No wheel for {string.Join(", ", files)}.");
                var view = new WheelView();
                view.ShowWheel(content, WheelTheme.For(dark), animate: false);
                view.SetHovered(hovered is { } target ? content.Options.ToList().FindIndex(option => option.Target == target) : -1);
                view.Measure(new Size(size, size));
                view.Arrange(new Rect(0, 0, size, size));

                var cell = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                cell.Render(ExplorerStandIn(dark, size));
                cell.Render(view);
                context.DrawImage(cell, new Rect((i % columns) * size, (i / columns) * size, size, size));
            }
        }

        Save(sheet, columns * size, (cells.Length + columns - 1) / columns * size, path);
    }

    /// <summary>A plain surface with rows of file names, so the wheel's translucency and shadow show.</summary>
    private static DrawingVisual ExplorerStandIn(bool dark, int size)
    {
        var visual = new DrawingVisual();
        var text = new SolidColorBrush(dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0, 0, 0));
        var typeface = new Typeface("Segoe UI");
        using var context = visual.RenderOpen();
        context.DrawRectangle(new SolidColorBrush(dark ? DarkSurface : LightSurface), null, new Rect(0, 0, size, size));
        string[] names = ["Holiday.jpg", "Report.pdf", "Logo.png", "Budget 2026.xlsx", "Scan 1.png", "Notes.txt"];
        for (var row = 0; row * 22 < size; row++)
        {
            var line = new FormattedText(
                $"{names[row % names.Length]}      {names[(row + 3) % names.Length]}",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                text,
                1);
            context.DrawText(line, new Point(10, 6 + (row * 22)));
        }

        return visual;
    }

    private static void RenderToasts(ThemeMode theme, bool dark, string folder, string suffix)
    {
        var progress = new ToastWindow { ThemeMode = theme };
        progress.ShowProgress("Converting Report.pdf to JPG", "Page 2 of 3…");
        progress.UpdateProgress("Page 2 of 3…", 0.45);
        RenderToast(progress, dark, Path.Combine(folder, $"toast-progress-{suffix}.png"));

        var saved = new ToastWindow { ThemeMode = theme };
        saved.ShowResult(new ToastContent(ToastKind.Success, "Saved Holiday.png", "1.2 MB", [new ToastAction("Show in folder", () => { })]));
        RenderToast(saved, dark, Path.Combine(folder, $"toast-saved-{suffix}.png"));

        var needFolder = new ToastWindow { ThemeMode = theme };
        needFolder.ShowResult(new ToastContent(
            ToastKind.Warning,
            "Can't save in Program Files",
            $"{AppIdentity.Name} can't save files there. Choose another folder for the converted copy.",
            [new ToastAction("Choose folder…", () => { }, IsPrimary: true)]));
        RenderToast(needFolder, dark, Path.Combine(folder, $"toast-folder-{suffix}.png"));

        var failed = new ToastWindow { ThemeMode = theme };
        failed.ShowResult(new ToastContent(
            ToastKind.Error,
            "Couldn't convert to PNG",
            "Holiday.jpg couldn't be read. The file may be damaged."));
        RenderToast(failed, dark, Path.Combine(folder, $"toast-error-{suffix}.png"));
    }

    /// <summary>
    /// The whole toast window, shadow margin included. A VisualBrush would crop it: the shadow effect
    /// makes the content's bounds larger than its layout box.
    /// </summary>
    private static void RenderToast(ToastWindow toast, bool dark, string path)
    {
        toast.Left = -10000;
        toast.Top = -10000;
        toast.Show();
        try
        {
            toast.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            var width = (int)Math.Ceiling(toast.ActualWidth);
            var height = (int)Math.Ceiling(toast.ActualHeight);
            var surface = new DrawingVisual();
            using (var context = surface.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(dark ? DarkSurface : LightSurface), null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            bitmap.Render((Visual)toast.Content); // Drawn at its margin's offset, like in the window.
            SaveBitmap(bitmap, path);
        }
        finally
        {
            toast.Close();
        }
    }

    /// <summary>
    /// One column per icon size, enlarged with nearest-neighbor scaling so single pixels stay visible.
    /// Rows: dark taskbar, dark taskbar paused, light taskbar, light taskbar paused.
    /// </summary>
    private static void RenderTrayIconSheet(string path)
    {
        const int cell = 192;
        const int padding = 16;
        (bool Light, bool Dimmed)[] rows = [(false, false), (false, true), (true, false), (true, true)];

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        var width = padding + (IconSizes.Length * (cell + padding));
        var height = padding + (rows.Length * (cell + padding));
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Gray, null, new Rect(0, 0, width, height));
            for (var row = 0; row < rows.Length; row++)
            {
                var (light, dimmed) = rows[row];
                var top = padding + (row * (cell + padding));
                context.DrawRectangle(new SolidColorBrush(light ? LightSurface : DarkSurface), null, new Rect(0, top - (padding / 2), width, cell + padding));
                for (var column = 0; column < IconSizes.Length; column++)
                {
                    var size = IconSizes[column];
                    var scaled = cell / size * size;
                    var left = padding + (column * (cell + padding)) + ((cell - scaled) / 2);
                    var icon = Decode(TrayIconRenderer.RenderPng(size, light, dimmed));
                    context.DrawImage(icon, new Rect(left, top + ((cell - scaled) / 2), scaled, scaled));
                }
            }
        }

        Save(visual, width, height, path);
    }

    private static void RenderWindow(Window window, bool dark, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        try
        {
            // Let layout finish before taking the picture.
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            var content = (FrameworkElement)window.Content;
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // Mica isn't part of a RenderTargetBitmap, so paint its base color instead.
                context.DrawRectangle(new SolidColorBrush(dark ? DarkSurface : LightSurface), null, bounds);
                context.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, bounds);
            }

            Save(visual, (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), path);
        }
        finally
        {
            window.Close();
        }
    }

    private static BitmapFrame Decode(byte[] png) =>
        BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

    private static void Save(Visual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        SaveBitmap(bitmap, path);
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
#endif
