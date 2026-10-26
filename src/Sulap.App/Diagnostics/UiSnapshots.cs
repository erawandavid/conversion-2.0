#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sulap.App.Tray;
using Sulap.App.Views;
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
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
#endif
