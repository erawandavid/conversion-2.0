namespace Sulap.Core.Settings;

/// <summary>User settings, stored as JSON in <see cref="AppIdentity.DataDirectory"/>.</summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    /// <summary>Schema version of the settings file, for future migrations.</summary>
    public int Version { get; init; } = CurrentVersion;

    public TriggerKeys Triggers { get; init; } = new();
}

/// <summary>Which key, pressed during a file drag, opens which wheel.</summary>
public sealed record TriggerKeys
{
    public TriggerKey Convert { get; init; } = TriggerKey.Shift;

    public TriggerKey Tools { get; init; } = TriggerKey.Ctrl;

    public TriggerKey Actions { get; init; } = TriggerKey.Alt;
}

public enum TriggerKey
{
    None,
    Shift,
    Ctrl,
    Alt,
}
