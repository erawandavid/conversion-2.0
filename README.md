# Sulap

Sulap is a Windows tray app that converts files with a drag gesture. While you drag a file in File Explorer or on the desktop, press **Shift**: a radial wheel appears at the cursor with the formats the file can become. Drop the file on a wedge and a converted copy appears next to the original. Press **Ctrl** during the same drag for a Tools wheel (compress, crop, add background…). Everything runs offline, on your device.

> **Status:** early development. The Convert wheel (Shift) works; the Tools wheel (Ctrl) isn't built yet. There is no installer: each pull request's CI run has a ready-to-run test build under **Artifacts**.

## Using it

1. Start Sulap. Its icon sits in the notification area; Windows may hide it in the ^ overflow.
2. Start dragging a file in File Explorer or on the desktop: a PDF, an image (JPG, PNG, WEBP, HEIC, AVIF, TIFF, BMP, GIF), or a PowerPoint, Excel, or Word file.
3. While you drag, press and release **Shift**. The Convert wheel opens at the pointer.
4. Drop the file on a format. The converted copy is saved next to the original, and a notice in the corner of the screen says what was saved.

What each kind of file can become:

| From | To |
| --- | --- |
| PDF | DOCX, JPG, PNG, TXT |
| Image | the other image formats, PDF, DOCX |
| PowerPoint (.pptx, .ppt) | PDF, JPG, PNG (one image per slide) |
| Excel (.xlsx, .xls) | PDF, CSV (one file per sheet) |
| Word (.docx, .doc) | PDF, TXT |

Good to know:

- The original is never changed, moved, or overwritten. When a name is taken, the copy gets a number, such as `Holiday (2).png`.
- Several images dropped on **PDF** become one PDF, with the pages in File Explorer's name order. A PDF with several pages dropped on **JPG** or **PNG** becomes a folder of pages, such as `Report (JPG)`.
- **PDF → DOCX** keeps the layout when Microsoft Word is installed: Sulap runs a hidden copy of Word and closes it afterwards. While Word converts, Sulap turns off Word's "Word will now convert your PDF…" message, and turns it back on when it's done. Without Word, only the text is kept.
- **PowerPoint, Excel, and Word files** are converted by the installed Microsoft Office program, which runs hidden for a few seconds on a temporary copy of your file. Macros are switched off. Without the program, the wedge is greyed out and says what it needs.
  - A presentation becomes a PDF, or a folder with one image per slide (`Deck (PNG)\Slide 1.png`). Hidden slides are left out, and the notice says so.
  - A workbook becomes a PDF of its printable sheets, or CSV files: one file when only one sheet has data, otherwise a folder with a file per sheet (`Budget (CSV)\Data.csv`). Hidden and empty sheets are left out. Like Excel's own Save As, the CSV uses Windows' list separator, which is a semicolon on some regional settings.
  - Word's text export writes a table row on one line, with tabs between the cells.
  - If PowerPoint is already open, Sulap uses it: it opens a copy of your file without a window, closes only that copy, and leaves PowerPoint and your presentations alone. Word and Excel always get a separate hidden copy.
  - A file protected by a password can't be converted, and Sulap says so.
- A file Sulap can't convert (a ZIP, a text file) gets a small notice instead of a wheel. Dropping on it does nothing, so nothing is moved by mistake.
- Scanned PDFs get their text from Windows' built-in text recognition.
- Saving **HEIC** needs the HEIF and HEVC extensions from the Microsoft Store, and it drops the photo's camera, date, and location details.
- When the original's folder can't be written to, the notice offers **Choose folder…**.
- **Pause** in the tray menu turns the wheel off until you choose it again.

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

The command-line tool does the same conversions, which helps for batches and scripts. Run it without arguments for the full list of options:

```
dotnet run --project src/Sulap.Cli -- convert Holiday.jpg --to png
dotnet run --project src/Sulap.Cli -- info Report.pdf
```

## Contributing

After cloning, enable the repository's git hooks once:

```
git config core.hooksPath .githooks
```

The `commit-msg` hook removes AI attribution lines from commit messages. Don't bypass it with `--no-verify`.

## License

[MIT](LICENSE)
