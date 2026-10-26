# Sulap — project brief

The original brief from the owner (erawandavid), saved verbatim on 2026-10-26. Decisions made after it was written are listed under [Amendments](#amendments) at the end; where they differ, the amendments win.

---

Build a native Windows desktop app from scratch in this folder (so far it only contains `.claude/settings.json`). Talk to me in Bahasa Indonesia; write all code, comments, README, and commit messages in English. I'm on a Windows laptop (x64) and you're running natively on Windows, not in WSL.

## What we're building
**Sulap** (working name — keep it in one place so I can rename it later) is a tray app that converts files with a drag gesture. While I drag a file in File Explorer or on the Desktop and press Shift, a radial wheel pops up at the cursor showing what the file can be converted to. I drop the file on a wedge and a converted copy appears next to the original. Pressing Ctrl during the same drag flips to a Tools wheel (compress, crop, add background…). Everything runs offline, on-device.

It's inspired by a Mac app (Tangerine). Don't copy its name, logo, icon, marketing text, or assets — give Sulap its own identity and a native Windows 11 look.
- GitHub user: erawandavid. One friend of mine is the only other contributor.
- Microsoft Word (Office 2024) is installed on my laptop — use it as an optional high-quality engine (see below), but the app must also work without Office.

## Reference behavior (from a demo video of the Mac app)
1. Drag `Page 001.pdf` + press Shift → wheel with 4 wedges: DOCX (top), JPG (right), PNG (bottom), TXT (left). The center hub shows the hovered option. Drop on JPG → `Page 001.jpg` appears beside the PDF.
2. Drag `Page 001.jpg` + Shift → 8 wedges: PNG, WEBP, HEIC, TIFF, AVIF, BMP, PDF, DOCX.
3. Same drag, press Ctrl → Tools wheel, 7 wedges (icon above a small uppercase label): COMPRESS, METADATA, EDIT, ANNOTATE, ADD BG, CROP, REDACT. Idle hub shows the file size ("1.2 MB"); hovering shows the tool name.
4. Drop on CROP → small "Crop Image" window: the image with a draggable crop rect + corner/edge handles, aspect-ratio segmented control (Free, 1:1, 16:9, 9:16, 4:3, 3:4), W × H px fields, Reset and Apply. Apply → `Page 001 Cropped.jpg`.
5. Drag that result + Ctrl, drop on ADD BG → window with a live preview of the image on a background; Background swatches (a row of gradient presets generated in code + "pick image", a row of solid colors + custom color), sliders for Padding / Corner radius / Shadow showing px values, Ratio segmented (Auto, 1:1, 16:9, 4:3), Reset and Save → `Page 001 Cropped With Background.png`.

Look & feel: a ~280 px circular wheel with a frosted, semi-translucent look (fake the glass with layered fills, a highlight and a soft shadow — real blur-behind is an optional experiment), wedges with rounded corners and small gaps, a thin outer rim, a round hub with a pill label. The hovered wedge fills with an accent color (default: the Windows accent color). Segoe UI Variable for text, Segoe Fluent Icons for tool icons (fallback Segoe MDL2 Assets on Windows 10). Pop-in animation (scale + fade, slight overshoot), fast fade-out; respect "Animation effects" being off. Tool windows: compact, Windows 11 style (rounded corners, Mica if possible), same accent. Follow the Windows light/dark app mode.

## Tech decisions (keep these unless you hit a real blocker — then ask me)
- C# / .NET 10 (LTS), WPF, TFM `net10.0-windows10.0.19041.0` (so we can call WinRT APIs). Windows 10 2004+ / Windows 11.
- Layout: `src/Sulap.Core` (conversion + tools engine, no UI), `src/Sulap.Cli` (`sulap convert <file> --to jpg`, `sulap tool compress <file>`, `sulap info <file>` — so you can test the engine yourself in the terminal), `src/Sulap.App` (WPF tray app), `tests/Sulap.Core.Tests` (xUnit).
- Tray app with no main window; tray menu: Settings…, About, Start with Windows (HKCU Run key), Pause, Quit. Single instance (named mutex). Per-Monitor V2 DPI awareness and long-path awareness in the manifest. Must run as a normal user, never elevated (an elevated window can't receive drops from Explorer).
- Settings as JSON in `%AppData%\Sulap`. Zero network calls, no analytics.
- A format registry maps each input type (extension + content sniffing) to its output formats and tools; the wheels are built from it — nothing hard-coded in the UI. Max 8 wedges per wheel.
- Pre-approved NuGet packages: Magick.NET (Q8, AnyCPU) for images, PdfPig (PDF text), DocumentFormat.OpenXml (DOCX), PDFsharp (create/edit PDFs), xUnit. Ask me before adding anything else.
- Built-in Windows APIs first: `Windows.Data.Pdf` (render PDF pages), `Windows.Media.Ocr` (OCR), `System.IO.Compression` / `System.Formats.Tar` (archives).

## The hard part: noticing a file drag system-wide and catching the drop
Windows has no reliable public "a drag started" event, so combine signals — and prove each one with a logging test before building on it:
1. Drag-start signal: an out-of-context `SetWinEventHook` for EVENT_OBJECT_SHOW/CREATE and HIDE/DESTROY, watching for the shell's drag-image window (class `SysDragImage` — undocumented, so verify it on my machine). Fallback: a `WH_MOUSE_LL` hook on its own background thread with its own message loop (left button down + moved past SM_CXDRAG/SM_CYDRAG). Keep hook callbacks tiny — Windows silently removes slow hooks, and a slow hook lags the mouse system-wide.
2. While a drag may be active, poll `GetAsyncKeyState` for Shift / Ctrl / Alt / Esc on a ~60 Hz timer.
3. When a trigger key is pressed, show the wheel window under the cursor in an invisible "sensor" state. Only when it receives `DragEnter` with `DataFormats.FileDrop` (real files) is it a file drag → animate the wheel in and build it from the dragged files. Anything else (text, virtual files from zips/Outlook) → hide immediately so the drop goes where it normally would.
4. Shift → Convert wheel. Ctrl → Tools wheel. Alt → Actions wheel (later phase). The last pressed key wins; releasing keys keeps the wheel open. Keys configurable in Settings. Hide the wheel on drop, when the drag ends (drag-image window gone / mouse button released), or on Esc.

Window details:
- WPF window: `WindowStyle=None`, `AllowsTransparency=True`, `Topmost`, `ShowActivated=False`, `ShowInTaskbar=False`, plus `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` — it must never steal focus from Explorer. Place it with physical pixels from `GetCursorPos`, on the right monitor, clamped inside the work area.
- Fully transparent pixels are hit-test transparent: in sensor state fill with alpha 1 (`#01000000`); in wheel state only the ring and hub are hit-testable, so drops outside the ring fall through to the desktop. Keep Explorer's drag thumbnail above the wheel (z-order the wheel just under the `SysDragImage` window, or use `IDropTargetHelper`).
- `DragOver`: angle/distance from center → hovered wedge (hub and outside the ring = none). Set `e.Effects = DragDropEffects.Copy` on a wedge, `None` elsewhere. NEVER return Move: Shift means "move" in Explorer, and if the target reports Move, Explorer may delete the original. We only READ the source — never move, modify, or delete it.
- `Drop`: grab the file paths and return immediately — Explorer is blocked inside DoDragDrop until Drop returns. Do the work on a background task.
- Debug builds get a "Playground" window where I can drop/pick a file and open the wheel in-window, to test the UI without the global hooks.

## Output rules
- Write into the same folder as the original. Conversions keep the base name (`Report.pdf` → `Report.jpg`). Tools add a suffix: ` Cropped`, ` Compressed`, ` Edited`, ` Annotated`, ` Redacted`, ` Clean` (metadata removed), ` With Background`.
- Never overwrite: on a name clash use ` (2)`, ` (3)`…. Write to a temp file in the same folder, rename it into place, then call `SHChangeNotify` so Explorer shows it right away.
- If the folder isn't writable (e.g. Program Files), ask where to save instead.
- Multi-page PDF → images: a folder `Report (JPG)\` with `Page 1.jpg`, `Page 2.jpg`…, 300 DPI by default.
- Several files dragged together: the wheel shows only options valid for all of them; process as a batch. Several images → PDF makes one combined PDF.
- After each job, a small toast near the cursor/tray: result name, size (plus "−72%" for compress), an "Open folder" button (`explorer /select,`); errors in plain language.
- Heavy work off the UI thread, with progress for long jobs. The UI never freezes.

## Conversions (all local)
- Images (Magick.NET): JPG, PNG, WEBP, HEIC, AVIF, TIFF, BMP, GIF, → PDF (PDFsharp), → DOCX (image embedded, OpenXML). Respect EXIF orientation, keep color profiles, flatten transparency onto white for JPG/BMP. Check `MagickNET.SupportedFormats` at runtime and hide outputs this build can't write (HEIC encoding may be missing — check whether Windows' WIC HEIF encoder can be a fallback).
- PDF (don't use Magick.NET for PDFs — it needs Ghostscript): → JPG/PNG per page (`Windows.Data.Pdf`), → TXT (PdfPig; OCR with `Windows.Media.Ocr` for pages without a text layer), → DOCX:
  - Word installed: Word COM automation (late-bound via ProgID, dedicated STA thread, invisible, no dialogs, timeout, always Quit) — Word's PDF Reflow keeps the layout far better.
  - No Word: PdfPig text blocks → OpenXML paragraphs (text-first, best effort — say so in the README).
- Docs: DOCX → PDF via Word (or LibreOffice if installed); TXT/MD ↔ DOCX/PDF in code. Formats with no available engine show as disabled with a hint.
- Video/audio (later phase): FFmpeg if it's on PATH (`winget install Gyan.FFmpeg`) — MP4, MOV, MKV, WEBM, AVI, GIF, MP3, M4A, WAV, FLAC, OGG, OPUS. Without FFmpeg, offer what `Windows.Media.Transcoding` handles (MP4, MP3, M4A, WAV, WMA) — verify each.
- Archives (later phase): ZIP / TAR / GZ create + extract with built-in .NET APIs.
- Run external tools with `ProcessStartInfo.ArgumentList` — never one concatenated command string, since file names can contain anything.

## Tools wheel
- COMPRESS: images = re-encode with a quality setting + optional max dimension; PDF = research the best offline option (e.g. Ghostscript if I have it installed — don't bundle it, it's AGPL); hide it if nothing is available. Keep the original if the result isn't smaller.
- METADATA: window listing EXIF/GPS/camera/dates (images) or author/creator/etc. (PDF), with "Remove location" and "Remove all".
- CROP: as in reference #4. ADD BG: as in reference #5, export PNG.
- EDIT: rotate/flip, resize, brightness/contrast/saturation/exposure with live preview.
- ANNOTATE: pen, highlighter, arrow, rectangle, text, colors, undo/redo.
- REDACT: draw boxes (black / blur / pixelate). For PDFs, redaction must truly remove content: rasterize the affected pages with the boxes burned in (Windows.Data.Pdf → image → PDFsharp), and document that. Bonus: suggest boxes for emails/phone numbers found with OCR + regex.

## Actions wheel (Alt)
Share (Windows share sheet), Copy file, Copy path, Move to…, Open in Terminal (`wt -d`), Show in Explorer.

## Git & GitHub rules
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

## Phases — after EACH phase: build, run tests, give me a short manual test checklist in Bahasa Indonesia, commit on the phase branch, push it, open the PR, and wait for my OK
0. Setup:
   - Check the Windows version, `dotnet --list-sdks`, `git`, `gh`; tell me what's missing (install with winget only after I agree).
   - Confirm my git identity, then make the bootstrap commit on `main`: `.gitignore` for .NET/Visual Studio (also ignoring `.claude/settings.local.json`), `.gitattributes`, the commit-msg hook, MIT `LICENSE`, a README stub, and `.claude/settings.json`.
   - Ask me for the repo name and visibility, create it with `gh repo create`, and push `main`. Ask for my friend's GitHub username; after I confirm, invite them with `gh api -X PUT repos/<owner>/<repo>/collaborators/<username>`.
   - On branch `phase-0-setup`: save this whole brief to `docs/SPEC.md`; create `CLAUDE.md` (build/test/run commands, architecture, conventions, and the Git & GitHub rules), the solution + 4 projects, and an empty tray app that builds and sits in the tray.
1. Detection spike: a small logging build that proves which drag signals work on my machine (SysDragImage, mouse hook, key polling, sensor DragEnter). I run it, drag some files, and paste the log back to you.
2. The magic: Convert wheel UI + hover + drop. Dropping only shows a toast ("would convert to JPG") — no real conversion yet. I test it by hand.
3. Real conversions for PDF and images (the reference flows) + output rules + toast. Engine unit tests with fixtures generated in code (a PDF with text, a JPEG with EXIF + GPS, a PNG with alpha).
4. Tools wheel: hub shows the file size; COMPRESS, METADATA, CROP, ADD BG first, then EDIT, ANNOTATE, REDACT.
5. More formats (docs via Word, video/audio, archives), batch, Actions wheel.
6. Polish: Settings (trigger keys, which formats appear and their order, default quality/DPI, start with Windows), first-run onboarding (a short "drag, then press Shift" animation — Shift *before* clicking does a range-select in Explorer), our own app/tray icon, `IDropTargetHelper` + drop descriptions ("Convert to JPG"), accessibility (UI Automation names, keyboard navigation in tool windows), English + Bahasa Indonesia resources, optional "Send to → Sulap" entry that opens the wheel for the selected files.
7. Release: GitHub Actions CI on `windows-latest` (`dotnet build` + `dotnet test`; tests needing Word/OCR/FFmpeg skip when unavailable). Release workflow on `v*` tags, which I create myself after merging: `dotnet publish` self-contained single-file for win-x64 (and win-arm64 if all dependencies support it), zip, attach to a GitHub Release (unsigned — README explains the SmartScreen "More info → Run anyway" step). Workflows only build and upload — they never commit or push. README with a demo GIF placeholder, features, install, build from source, roadmap.

## How to verify
- After every change, run `dotnet build` and `dotnet test`. Never tell me something works without building it.
- Check conversions yourself with the CLI on real files (`sulap convert`, then `sulap info` on the result).
- Add a debug argument (e.g. `--render-wheel <file> <out.png>`) that renders the wheel and tool windows to PNG with `RenderTargetBitmap`, and look at the PNG yourself.
- Real drag-from-Explorer behavior can only be tested by me — tell me exactly what to try.
- If a Windows API doesn't behave the way this brief assumes, tell me and propose an alternative instead of quietly changing the design.

Start with Phase 0. Before writing code, show me your plan for Phases 0–2 in a few bullets.

---

## Amendments

### 2026-10-26 — Phase 0
- **Development environment.** Claude Code runs in a Linux cloud container (Claude Code on the web, Ubuntu 24.04), not natively on Windows. Agreed approach:
  - Everything is compiled on Linux with `EnableWindowsTargeting=true` (the .NET 10 SDK comes from the Ubuntu archive).
  - Engine tests that don't need Windows run on Linux; tests that need Windows skip themselves there.
  - Windows-only behavior is verified by a build-and-test-only GitHub Actions workflow on `windows-latest` (brought forward from Phase 7 to Phase 0) and by the owner by hand on the laptop. The workflow never commits or pushes.
- **Repository.** It already existed: `erawandavid/conversion-2.0` (public, MIT `LICENSE` from GitHub's initial commit), so no `gh repo create`.
- **Collaborator.** The friend is `L3nvil`, already a collaborator, so no invite.
- **GitHub access.** `gh` doesn't work in the cloud container. PRs are opened, and CI is read, through the GitHub connector, which acts as `erawandavid`.
- **Git identity in the cloud.** The container's global identity is `Claude <noreply@anthropic.com>`, and it SSH-signs commits with the environment's key. With the owner's confirmation, this clone sets `user.name`, `user.email`, and `commit.gpgsign=false` in the repository's local config, so commits carry the owner's identity and nothing from the Claude environment.
- **`.claude/settings.json`** wasn't in the repository when the session started; the bootstrap commit added it with commit and PR attribution empty and `sessionUrl: false`.
