namespace Sulap.Core.Tests;

public sealed class AppIdentityTests
{
    [Fact]
    public void Name_comes_from_the_build_metadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppIdentity.Name));
        Assert.Equal(AppIdentity.Name.Trim(), AppIdentity.Name);
    }

    [Fact]
    public void Data_directory_is_named_after_the_app()
    {
        Assert.Equal(AppIdentity.Name, Path.GetFileName(AppIdentity.DataDirectory));
        Assert.Equal(AppIdentity.DataDirectory, Path.GetDirectoryName(AppIdentity.LogDirectory));
    }

    [Fact]
    public void Version_is_a_plain_version_number()
    {
        Assert.DoesNotContain('+', AppIdentity.Version);
        Assert.True(Version.TryParse(AppIdentity.Version, out _), AppIdentity.Version);
    }
}
