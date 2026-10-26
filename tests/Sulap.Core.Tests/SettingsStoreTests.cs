using Sulap.Core.Settings;

namespace Sulap.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "settings-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults_and_creates_the_file()
    {
        var store = new SettingsStore(_directory);

        var settings = store.LoadOrCreate();

        Assert.Equal(new AppSettings(), settings);
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Saved_settings_load_back_unchanged()
    {
        var store = new SettingsStore(_directory);
        var saved = new AppSettings { Triggers = new TriggerKeys { Convert = TriggerKey.Alt, Actions = TriggerKey.None } };

        store.Save(saved);

        Assert.Equal(saved, store.LoadOrCreate());
    }

    [Fact]
    public void File_is_readable_json_with_key_names()
    {
        var store = new SettingsStore(_directory);

        store.Save(new AppSettings());
        var json = File.ReadAllText(store.FilePath);

        Assert.Contains("\"triggers\"", json);
        Assert.Contains("\"convert\": \"Shift\"", json);
        Assert.Contains("\"tools\": \"Ctrl\"", json);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_replaced_with_defaults()
    {
        var store = new SettingsStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "{ this is not json");

        var settings = store.LoadOrCreate();

        Assert.Equal(new AppSettings(), settings);
        Assert.Equal("{ this is not json", File.ReadAllText(store.BackupPath));
        Assert.Equal(settings, store.LoadOrCreate());
    }

    [Fact]
    public void Unknown_key_name_counts_as_corrupt()
    {
        var store = new SettingsStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, """{ "version": 1, "triggers": { "convert": "Win" } }""");

        Assert.Equal(new AppSettings(), store.LoadOrCreate());
        Assert.True(File.Exists(store.BackupPath));
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new SettingsStore(_directory);

        store.Save(new AppSettings());

        Assert.Equal([store.FilePath], Directory.GetFiles(_directory));
    }
}
