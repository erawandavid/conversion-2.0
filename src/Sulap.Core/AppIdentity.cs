using System.Reflection;

namespace Sulap.Core;

/// <summary>
/// Who the app is. The name comes from the <c>AppName</c> MSBuild property in Directory.Build.props,
/// so renaming the app is a one-line change there.
/// </summary>
public static class AppIdentity
{
    private static readonly Assembly Assembly = typeof(AppIdentity).Assembly;

    /// <summary>Product name shown to users and used for the data folder, mutex, and Run key.</summary>
    public static string Name { get; } = ReadName();

    /// <summary>Version without the source revision suffix, e.g. "0.1.0".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Per-user data folder: <c>%AppData%\{Name}</c>.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    private static string ReadName() =>
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "AppName")?.Value
        ?? throw new InvalidOperationException("The AppName assembly metadata is missing; check Directory.Build.props.");

    private static string ReadVersion()
    {
        var version = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var revision = version.IndexOf('+');
        return revision < 0 ? version : version[..revision];
    }
}
