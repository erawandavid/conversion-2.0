using System.Globalization;

namespace Sulap.Core.Formatting;

public static class FileSize
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats a byte count like File Explorer does (1 KB = 1024 bytes), with about three significant
    /// digits: "812 bytes", "1.2 MB", "34.5 MB", "123 MB".
    /// </summary>
    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        provider ??= CultureInfo.CurrentCulture;

        if (bytes < 1024)
        {
            return string.Format(provider, "{0} {1}", bytes, bytes == 1 ? "byte" : "bytes");
        }

        var value = bytes / 1024d;
        var unit = 0;
        while (Round(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var rounded = Round(value);
        var number = rounded >= 100
            ? rounded.ToString("0", provider)
            : rounded.ToString("0.#", provider);
        return $"{number} {Units[unit]}";
    }

    private static double Round(double value) =>
        Math.Round(value, value >= 100 ? 0 : 1, MidpointRounding.AwayFromZero);
}
