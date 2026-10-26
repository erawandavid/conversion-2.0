using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sulap.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as a JSON file. A bad file never stops the app.</summary>
public sealed class SettingsStore(string directory)
{
    public const string FileName = "settings.json";

    public string FilePath { get; } = Path.Combine(directory, FileName);

    /// <summary>Where an unreadable settings file is kept before it gets replaced with defaults.</summary>
    public string BackupPath => FilePath + ".bak";

    public static SettingsStore ForCurrentUser() => new(AppIdentity.DataDirectory);

    /// <summary>
    /// Reads the settings. A missing file is created with defaults, so users can find it.
    /// A file that isn't valid JSON is kept as <see cref="BackupPath"/> and replaced with defaults.
    /// If the folder or file can't be accessed at all, defaults are used and nothing is written.
    /// </summary>
    public AppSettings LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    using var stream = File.OpenRead(FilePath);
                    return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings)
                        ?? throw new JsonException("The settings file contains null.");
                }
                catch (JsonException)
                {
                    // Keep the unreadable file for the user, then start over with defaults.
                    File.Copy(FilePath, BackupPath, overwrite: true);
                }
            }

            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings atomically: a temp file next to the target, then a rename.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tempPath = FilePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
        }

        File.Move(tempPath, FilePath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
