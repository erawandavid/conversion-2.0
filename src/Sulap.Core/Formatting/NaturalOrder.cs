using System.Globalization;

namespace Sulap.Core.Formatting;

public static class NaturalOrder
{
    /// <summary>
    /// Sorts names the way File Explorer does: numbers by value ("Page 2" before "Page 10"), letters
    /// ignoring case.
    /// </summary>
    public static IComparer<string> FileNames { get; } =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);
}
