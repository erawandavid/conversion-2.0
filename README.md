# Sulap

Sulap is a Windows tray app that converts files with a drag gesture. While you drag a file in File Explorer or on the desktop, press **Shift**: a radial wheel appears at the cursor with the formats the file can become. Drop the file on a wedge and a converted copy appears next to the original. Press **Ctrl** during the same drag for a Tools wheel (compress, crop, add background…). Everything runs offline, on your device.

> **Status:** early development — there is nothing to install yet. Each pull request's CI run has a ready-to-run test build under **Artifacts**.

## Build from source

Requirements: Windows 10 version 2004 or later and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
git clone https://github.com/erawandavid/conversion-2.0.git
cd conversion-2.0
git config core.hooksPath .githooks
dotnet build Sulap.slnx
dotnet test
dotnet run --project src/Sulap.App
```

The app has no main window: look for its icon in the notification area (you may need to open the ^ overflow).

## Contributing

After cloning, enable the repository's git hooks once:

```
git config core.hooksPath .githooks
```

The `commit-msg` hook removes AI attribution lines from commit messages. Don't bypass it with `--no-verify`.

## License

[MIT](LICENSE)
