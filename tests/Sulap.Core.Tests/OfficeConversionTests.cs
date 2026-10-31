using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Sulap.Core.Conversion;
using Sulap.Core.Formats;
using UglyToad.PdfPig;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Sulap.Core.Tests;

/// <summary>
/// Converts real Excel, Word, and PowerPoint files with the Office programs, which start hidden and
/// close again. They only run with SULAP_TEST_OFFICE=1 on a machine that has Office, and are best run
/// while no Office program is open: they check that none is left running.
/// </summary>
[Collection("Office")] // Tests that start Office programs run one at a time, here and in ConverterTests.
public sealed class OfficeConversionTests : IDisposable
{
    private static readonly Lazy<EngineCapabilities> Detected = new(EngineCapabilities.Detect);

    private readonly TempFolder _folder = new();

    public OfficeConversionTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Conversions use Windows engines.");
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("SULAP_TEST_OFFICE") == "1";

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Excel_sheets_become_csv_files_and_hidden_or_empty_sheets_are_left_out()
    {
        Assert.SkipUnless(Enabled && Detected.Value.Excel, "Set SULAP_TEST_OFFICE=1 on a machine with Excel to run this.");
        var source = Workbook(_folder.File("Budget.xlsx"));

        var result = await Convert(FileFormat.Csv, source);

        var output = Assert.Single(result.Outputs);
        Assert.True(output.IsFolder);
        Assert.Equal(_folder.File("Budget (CSV)"), output.Path);
        Assert.Equal(["Data.csv", "Totals.csv"], Directory.GetFiles(output.Path).Select(Path.GetFileName).Order().ToArray());
        var data = File.ReadAllText(Path.Combine(output.Path, "Data.csv"));
        Assert.Contains("Pens", data);
        Assert.Contains("\"say \"\"hi\"\"\"", data); // Quotes are kept.
        Assert.DoesNotContain("hidden sheet", string.Concat(Directory.GetFiles(output.Path).Select(File.ReadAllText)));
        Assert.Contains(result.Notes, note => note.Contains("2 sheets") && note.Contains("left out"));
        AssertNoOfficeLeftovers();
    }

    [Fact]
    public async Task A_workbook_becomes_a_pdf_of_its_printable_sheets()
    {
        Assert.SkipUnless(Enabled && Detected.Value.Excel, "Set SULAP_TEST_OFFICE=1 on a machine with Excel to run this.");
        var source = Workbook(_folder.File("Budget.xlsx"));

        var output = Assert.Single((await Convert(FileFormat.Pdf, source)).Outputs);

        using var pdf = PdfDocument.Open(output.Path);
        Assert.Equal(2, pdf.NumberOfPages); // Data and Totals; not the hidden or the empty sheet.
        Assert.Contains("Pens", pdf.GetPage(1).Text);
        AssertNoOfficeLeftovers();
    }

    [Fact]
    public async Task A_workbook_without_data_says_so_instead_of_saving_an_empty_file()
    {
        Assert.SkipUnless(Enabled && Detected.Value.Excel, "Set SULAP_TEST_OFFICE=1 on a machine with Excel to run this.");
        var source = Workbook(_folder.File("Blank.xlsx"), withData: false);

        var result = await Convert(FileFormat.Csv, source);

        Assert.Empty(result.Outputs);
        Assert.Contains("no data", Assert.Single(result.Failures).Message);
        Assert.Equal(["Blank.xlsx"], _folder.Names());
        AssertNoOfficeLeftovers();
    }

    [Fact]
    public async Task A_word_document_becomes_a_pdf_and_a_text_file()
    {
        Assert.SkipUnless(Enabled && Detected.Value.Word, "Set SULAP_TEST_OFFICE=1 on a machine with Word to run this.");
        var source = Document(_folder.File("Letter.docx"));

        var pdf = Assert.Single((await Convert(FileFormat.Pdf, source)).Outputs);
        var txt = Assert.Single((await Convert(FileFormat.Txt, source)).Outputs);

        using (var document = PdfDocument.Open(pdf.Path))
        {
            Assert.Contains("Quarterly letter", document.GetPage(1).Text);
        }

        var text = File.ReadAllText(txt.Path);
        Assert.Contains("Quarterly letter", text);
        Assert.Contains("Cell A\tCell B", text); // A table row stays on one line.
        Assert.DoesNotContain("\r\r", text);
        Assert.DoesNotContain("\n\n\n", text.Replace("\r\n", "\n"));
        AssertNoOfficeLeftovers();
    }

    [Fact]
    public async Task A_presentation_becomes_a_pdf_and_one_image_per_visible_slide()
    {
        Assert.SkipUnless(Enabled && Detected.Value.PowerPoint, "Set SULAP_TEST_OFFICE=1 on a machine with PowerPoint to run this.");
        var source = Presentation(_folder.File("Deck.pptx"), slides: 3, hiddenSlide: 2);

        var pdf = Assert.Single((await Convert(FileFormat.Pdf, source)).Outputs);
        var png = await Convert(FileFormat.Png, source);

        using (var document = PdfDocument.Open(pdf.Path))
        {
            Assert.Equal(2, document.NumberOfPages);
        }

        var slides = Assert.Single(png.Outputs);
        Assert.True(slides.IsFolder);
        Assert.Equal(["Slide 1.png", "Slide 3.png"], Directory.GetFiles(slides.Path).Select(Path.GetFileName).Order().ToArray()); // Numbers are the slides' own.
        Assert.Equal("2 slides", slides.ItemsText);
        Assert.Contains(png.Notes, note => note.Contains("hidden") && note.Contains("left out"));
        AssertNoOfficeLeftovers();
    }

    [Fact]
    public async Task A_file_that_cannot_be_opened_fails_with_a_plain_message_and_leaves_nothing_behind()
    {
        Assert.SkipUnless(Enabled && Detected.Value.Excel, "Set SULAP_TEST_OFFICE=1 on a machine with Excel to run this.");
        var source = _folder.File("Broken.xlsx");
        File.WriteAllText(source, "This is not a workbook.");

        var result = await Convert(FileFormat.Csv, source);

        Assert.Empty(result.Outputs);
        Assert.Contains("couldn't be opened in Microsoft Excel", Assert.Single(result.Failures).Message);
        Assert.Equal(["Broken.xlsx"], _folder.Names());
        AssertNoOfficeLeftovers();
    }

    [Fact]
    public async Task The_original_is_untouched_by_an_office_conversion()
    {
        Assert.SkipUnless(Enabled && Detected.Value.Excel, "Set SULAP_TEST_OFFICE=1 on a machine with Excel to run this.");
        var source = Workbook(_folder.File("Budget.xlsx"));
        var before = (File.ReadAllBytes(source), File.GetLastWriteTimeUtc(source));

        await Convert(FileFormat.Pdf, source);

        Assert.Equal(before.Item1, File.ReadAllBytes(source));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(source));
    }

    private Task<ConversionResult> Convert(FileFormat target, params string[] sources) =>
        new Converter(new FormatRegistry(Detected.Value)).ConvertAsync(new ConversionRequest(sources, target), progress: null, TestContext.Current.CancellationToken);

    /// <summary>No working folder is left in TEMP, and (after a moment, as the programs take a second to exit) no program is left running.</summary>
    private static void AssertNoOfficeLeftovers()
    {
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), $"{AppIdentity.Name}-office-*"));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (OfficeProcesses().Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
        }

        Assert.Empty(OfficeProcesses());
    }

    private static List<string> OfficeProcesses() =>
        [.. new[] { "WINWORD", "EXCEL", "POWERPNT", "PDFREFLOW" }.SelectMany(name => System.Diagnostics.Process.GetProcessesByName(name)).Select(process => process.ProcessName)];

    // ---- Files made in code ----

    /// <summary>Data and Totals have data and are visible; Secret has data and is hidden; Empty is visible and empty.</summary>
    private static string Workbook(string path, bool withData = true)
    {
        using var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook(new S.Sheets());
        var sheets = workbookPart.Workbook.GetFirstChild<S.Sheets>()!;
        uint id = 1;
        void Add(string name, string[][] rows, bool hidden = false)
        {
            var part = workbookPart.AddNewPart<WorksheetPart>();
            part.Worksheet = new S.Worksheet(new S.SheetData(rows.Select((row, index) =>
                new S.Row(row.Select(text => new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) })) { RowIndex = (uint)index + 1 })));
            sheets.Append(new S.Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = id++, Name = name, State = hidden ? S.SheetStateValues.Hidden : S.SheetStateValues.Visible });
        }

        Add("Data", withData ? [["Item", "Note"], ["Pens", "say \"hi\""]] : []);
        Add("Totals", withData ? [["Total"], ["Pens: 1"]] : []);
        Add("Secret", withData ? [["hidden sheet"]] : [], hidden: true);
        Add("Empty", []);
        workbookPart.Workbook.Save();
        return path;
    }

    private static string Document(string path)
    {
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        static W.Paragraph P(string text) => new(new W.Run(new W.Text(text)));
        static W.TableCell Cell(string text) => new(P(text));
        main.Document = new W.Document(new W.Body(
            P("Quarterly letter"),
            P("Closing words"),
            new W.Table(
                new W.TableProperties(new W.TableBorders(new W.TopBorder { Val = W.BorderValues.Single }, new W.BottomBorder { Val = W.BorderValues.Single })),
                new W.TableRow(Cell("Cell A"), Cell("Cell B")),
                new W.TableRow(Cell("Cell C"), Cell("Cell D"))),
            P("The end")));
        main.Document.Save();
        return path;
    }

    /// <summary>
    /// Made by PowerPoint itself: a valid presentation is a lot of boilerplate to write by hand. The
    /// PowerPoint started here is ended again, and only that one: it would otherwise stay open while
    /// references from these late-bound calls linger, and the conversions would share it.
    /// </summary>
    private static string Presentation(string path, int slides, int hiddenSlide)
    {
        var before = PowerPointIds();
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
        var started = PowerPointIds().Except(before).ToList();
        try
        {
            dynamic presentation = app.Presentations.Add(0);
            for (var i = 1; i <= slides; i++)
            {
                dynamic slide = presentation.Slides.Add(i, 12); // ppLayoutBlank
                slide.Shapes.AddTextbox(1, 50, 50, 600, 100).TextFrame.TextRange.Text = $"Slide {i}";
            }

            presentation.Slides.Item(hiddenSlide).SlideShowTransition.Hidden = -1;
            presentation.SaveAs(path, 24); // ppSaveAsOpenXMLPresentation
            presentation.Close();
        }
        finally
        {
            app.Quit();
            foreach (var id in started)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(id);
                    if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
                    {
                        process.Kill();
                        process.WaitForExit(TimeSpan.FromSeconds(5));
                    }
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            }
        }

        return path;
    }

    private static HashSet<int> PowerPointIds()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("POWERPNT");
        var ids = processes.Select(process => process.Id).ToHashSet();
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return ids;
    }
}
