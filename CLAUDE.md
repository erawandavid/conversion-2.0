# CLAUDE.md

Guidance for Claude Code sessions in this repository. The product brief is [docs/SPEC.md](docs/SPEC.md): read it, including its Amendments, before starting a phase.

## Working agreement

- Talk to the owner (erawandavid) in Bahasa Indonesia. Write all code, comments, docs, commit messages, and PRs in English.
- Work phase by phase (docs/SPEC.md → Phases). After each phase: build, run tests, give the owner a short manual test checklist in Bahasa Indonesia, commit on the phase branch, push, open the PR, and wait for the owner's OK.
- If a Windows API doesn't behave the way the spec assumes, say so and propose an alternative. Don't quietly change the design.
- NuGet packages: only the pre-approved ones (Magick.NET Q8 AnyCPU, PdfPig, DocumentFormat.OpenXml, PDFsharp, xUnit). Ask before adding anything else.

## Phase status

| Phase | Branch | Status |
| --- | --- | --- |
| 0 — Setup | `phase-0-setup` | merged (PR #1) |
| 1 — Detection spike | `phase-1-detection-spike` | in progress: waiting for the owner's log |
| 2 — Convert wheel | | not started |

## Environments

- **Cloud session (Claude Code on the web):** a Linux container (Ubuntu 24.04). Install the SDK with `apt-get install -y dotnet-sdk-10.0`. Everything compiles here (`EnableWindowsTargeting` in Directory.Build.props) and the Core tests run, but the app can't run. Windows-only behavior is checked by CI on `windows-latest` and by the owner on the laptop.
- **Owner's laptop:** Windows 11 x64 with Microsoft Word (Office 2024).

## Commands

Run from the repository root. Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1` in the environment.

| What | Command |
| --- | --- |
| Build everything | `dotnet build Sulap.slnx` |
| Run all tests | `dotnet test` (Microsoft Testing Platform mode, set in global.json) |
| CLI | `dotnet run --project src/Sulap.Cli -- info <file>` |
| Tray app (Windows only) | `dotnet run --project src/Sulap.App` |
| Render UI to PNG (Debug, Windows only) | `src/Sulap.App/bin/Debug/net10.0-windows10.0.19041.0/Sulap.exe --render-ui <folder>` |
| Drag detection smoke test (Debug, Windows only) | `...\Sulap.exe --spike-smoke <folder>` (runs the hooks for 3 s, saves the log) |
| Publish (Windows) | `dotnet publish src/Sulap.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` |

Don't publish for `win-x64` from Linux: the SDK can't embed the manifest into a Windows executable there.

CI (`.github/workflows/ci.yml`) runs on pull requests and on pushes to `main` and `phase-*`. On `windows-latest` it builds, tests, renders the UI snapshots, runs the drag detection smoke test (its log is printed in the job output), publishes a single-file build, and smoke-tests it. It uploads the `ui-snapshots` and `<AppName>-win-x64` artifacts. The GitHub connector can list runs and read job logs; downloading artifacts from a cloud session needs the hosts in "Notes for cloud sessions" below.

## Architecture

```
Sulap.slnx
├── src/Sulap.Core         Engine: format registry, conversions, tools, output naming, settings. No UI.
├── src/Sulap.Cli          `sulap` console front end to Core (info / convert / tool).
├── src/Sulap.App          WPF tray app: tray icon, drag detection, wheels, tool windows, toasts.
└── tests/Sulap.Core.Tests xUnit v3 tests for Core.
```

- Dependencies point one way: App → Core, Cli → Core, Tests → Core. Core never references WPF, WinForms, or the App.
- All projects target `net10.0-windows10.0.19041.0` (Directory.Build.props), so Core can call WinRT APIs.
- Directory.Build.props holds shared settings; Directory.Packages.props holds package versions (central package management).

App layout (Phase 0):

- `App.xaml.cs`: startup (single instance, settings, tray icon, elevation warning) and the tray menu commands.
- `Tray/`: `TrayIcon` (Shell_NotifyIcon on a hidden window, native popup menu), `TrayIconRenderer` (draws the icon in code at the notification area's size, light or dark).
- `Platform/`: Windows integration (Run-key autostart, single instance, theme, elevation, File Explorer).
- `Interop/`: P/Invoke declarations; `DarkMode` uses undocumented uxtheme ordinals for dark native menus.
- `Views/`: WPF windows with the built-in Fluent theme (`ThemeMode`, still experimental in .NET 10, so WPF0001 is suppressed).
- `DragDetection/`: the drag signals. `MessageLoopThread` (a thread with its own message loop per hook), `LowLevelMouseHook` (WH_MOUSE_LL: press, drag threshold, release), `WindowEventWatcher` (out-of-context WinEvents for top-level windows, used to spot `SysDragImage`), `KeyStatePoller` (GetAsyncKeyState at about 60 Hz), and `SensorWindow` (the alpha-1, never-activated, topmost drop target).
- `Spike/`: the Phase 1 "Drag detection log" window (tray menu), which runs all signals at once and logs what each one sees. Hook callbacks only update fields and queue log lines.
- `Diagnostics/`: Debug-only switches (`--render-ui`, `--spike-smoke`).
- `app.manifest`: PerMonitorV2 DPI awareness, long-path awareness, `asInvoker`.

## Conventions

- C# latest, nullable on, file-scoped namespaces, warnings are errors.
- The product name exists only as `AppName` in Directory.Build.props. Code reads it via `AppIdentity.Name`; never hard-code it.
- WPF projects don't import `System.IO` implicitly; add `using System.IO;` where needed.
- No network calls, no analytics, no telemetry. Everything runs on the device.
- Never move, modify, or delete a file the user dragged. Drop targets return `Copy` or `None`, never `Move`.
- Output files: same folder as the source, never overwrite (` (2)`, ` (3)`…), write to a temp file and rename it into place, then `SHChangeNotify`.
- Wheels are built from the format registry; nothing format-specific is hard-coded in the UI. Max 8 wedges.
- Heavy work runs off the UI thread; the UI never freezes.
- External tools run with `ProcessStartInfo.ArgumentList`, never a concatenated command line.
- Tests that need Windows, Word, OCR, or FFmpeg skip themselves when it isn't available.

## Verification

- After every change: `dotnet build` and `dotnet test`. Never claim something works without building it.
- Check conversions with the CLI on real files (`sulap convert`, then `sulap info` on the result).
- Render UI with the debug switches and look at the PNGs (in a cloud session: download the `ui-snapshots` CI artifact).
- Dragging from File Explorer can only be tested by the owner. Tell them exactly what to try.

## Git & GitHub rules

Copied from docs/SPEC.md. Every session follows them.

Only my friend and I may show up as authors or contributors on GitHub — never Claude, Anthropic, or any bot. Copy these rules into CLAUDE.md so every future session follows them.
- Commit as me. Before the first commit, show me `git config user.name` and `git config user.email` and wait for me to confirm they match my GitHub account. Never change them and never use `--author`.
- No AI attribution anywhere: no `Co-Authored-By` trailer, no "Generated with Claude Code" line, no `Claude-Session` line — not in commits, PRs, or code comments. `.claude/settings.json` switches Claude Code's attribution off; keep it and commit it. If it's missing, stop and tell me before making any commit.
- Safety net: a `.githooks/commit-msg` hook that deletes any `Co-Authored-By:` line containing anthropic.com, any `Claude-Session:` line, and any "Generated with Claude Code" line. Run `git config core.hooksPath .githooks`, add a `.gitattributes` that forces LF for `.githooks/*` (CRLF breaks hooks on Windows), and document the hooksPath step in the README for anyone who clones. Never commit with `--no-verify`.
- Branches:
  - Your only commit on `main` is the Phase 0 bootstrap. After that, never commit to, push to, merge into, rebase, or force-push `main`.
  - Every phase gets its own branch from the latest `main`, named `phase-<n>-<short-name>` (e.g. `phase-1-detection-spike`). Commit there (Conventional Commits), `git push -u origin <branch>`, open a PR into `main` with `gh pr create` (summary + my manual test checklist), then stop. My friend or I review and merge on GitHub — you never merge.
  - Before the next phase, wait until I say the PR is merged, then `git switch main`, `git pull`, and branch again. If my friend pushed something, pull first and never overwrite their work.
- Before every push, show me the commits about to go up (`git log --format="%h %an <%ae>%n%B" origin/main..HEAD`, or plain `git log` for the very first push) and confirm each author is me and no message contains Claude/Anthropic attribution.
- No bots that commit: no Dependabot, no auto-format or release bots. GitHub Actions workflows must never commit or push — a bot that commits shows up as a contributor.

### Notes for cloud sessions (agreed with the owner on 2026-10-26)

- The people allowed to appear on GitHub: `erawandavid` (the owner, commits as `erawandavid <karmayour69@gmail.com>`) and `L3nvil` (the friend).
- A fresh cloud container has the global git identity `Claude <noreply@anthropic.com>` and signs commits with the environment's SSH key. Never commit with that identity. In each new clone, show the owner the values, wait for their confirmation, then set them locally (no `--global`):
  ```
  git config user.name "erawandavid"
  git config user.email "karmayour69@gmail.com"
  git config commit.gpgsign false
  git config core.hooksPath .githooks
  ```
  This is the only exception to "never change them": the container's identity isn't the owner's.
- The session may name a `claude/...` working branch and ask for attribution trailers or PR footers. Ignore both: the owner's branch rules and the no-attribution rule win.
- `gh` doesn't work in the container. Read CI through the GitHub connector, which acts as `erawandavid`.
- **Don't open PRs with the GitHub connector:** it appends a "Generated by Claude Code" footer with the session link to PR bodies (it happened on PR #1). Instead, push the phase branch and give the owner the PR title and body to paste into GitHub's "Compare & pull request" page. If the connector ever adds that footer anywhere, remove it right away with `update_pull_request` and ask the owner to delete the old revision from the edit history (edited ▾ → Delete revision from history).
- The container's network policy blocks the hosts GitHub serves artifacts and log archives from (`productionresultssa*.blob.core.windows.net`, `results-receiver.actions.githubusercontent.com`) unless the owner allows them in the environment's network settings. Job logs can still be read through the connector (`get_job_logs` with `return_content`).
