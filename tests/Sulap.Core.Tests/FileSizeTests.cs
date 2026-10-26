using System.Globalization;
using Sulap.Core.Formatting;

namespace Sulap.Core.Tests;

public sealed class FileSizeTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(812, "812 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_258_291, "1.2 MB")]
    [InlineData(36_175_872, "34.5 MB")]
    [InlineData(128_974_848, "123 MB")]
    [InlineData(1_048_575, "1 MB")]
    [InlineData(5_368_709_120, "5 GB")]
    public void Formats_like_explorer(long bytes, string expected)
    {
        Assert.Equal(expected, FileSize.Format(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Uses_the_culture_decimal_separator()
    {
        Assert.Equal("1,2 MB", FileSize.Format(1_258_291, CultureInfo.GetCultureInfo("id-ID")));
    }

    [Fact]
    public void Rejects_negative_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FileSize.Format(-1));
    }
}
