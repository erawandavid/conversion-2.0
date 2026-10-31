using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Sulap.Core.Conversion;

/// <summary>One paragraph of a page, with the look of its text.</summary>
internal sealed record TextParagraph(string Text, double FontSize, bool Bold);

/// <summary>The text of one page, as lines (for TXT) and paragraphs (for DOCX).</summary>
internal sealed record PageText(int Number, string Text, IReadOnlyList<TextParagraph> Paragraphs, bool FromOcr);

internal sealed record PdfTextResult(IReadOnlyList<PageText> Pages, IReadOnlyList<string> Notes);

/// <summary>
/// Reads a PDF's text layer with PdfPig. Pages without one (scans) go through Windows' OCR when it
/// has a language for the user; otherwise they stay empty and a note says so.
/// </summary>
internal static class PdfText
{
    private const double DefaultFontSize = 11;

    public static async Task<PdfTextResult> ReadAsync(string path, bool paragraphs, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        var pages = new List<PageText>();
        var ocrPages = new List<int>();
        var unreadPages = new List<int>();
        PdfPageRenderer? renderer = null;
        Windows.Media.Ocr.OcrEngine? engine = null;
        var engineChecked = false;

        try
        {
            using var document = Open(path);
            var count = document.NumberOfPages;
            for (var number = 1; number <= count; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report(new ConversionProgress((double)(number - 1) / count, $"Page {number} of {count}"));
                var page = document.GetPage(number);
                var text = Normalize(ContentOrderTextExtractor.GetText(page));
                if (text.Length > 0)
                {
                    pages.Add(new PageText(number, text, paragraphs ? Paragraphs(page) : [], FromOcr: false));
                    continue;
                }

                if (!engineChecked)
                {
                    engine = PageOcr.CreateEngine();
                    engineChecked = true;
                }

                if (engine is null)
                {
                    unreadPages.Add(number);
                    pages.Add(new PageText(number, string.Empty, [], FromOcr: false));
                    continue;
                }

                renderer ??= await PdfPageRenderer.OpenAsync(path, cancellationToken);
                var rendered = await renderer.RenderAsync(number - 1, 300, PageOcr.MaxSide, cancellationToken);
                var lines = await PageOcr.ReadLinesAsync(engine, rendered, cancellationToken);
                ocrPages.Add(number);
                pages.Add(new PageText(
                    number,
                    string.Join("\r\n", lines),
                    lines.Select(line => new TextParagraph(line, DefaultFontSize, Bold: false)).ToList(),
                    FromOcr: true));
            }
        }
        finally
        {
            renderer?.Dispose();
        }

        var notes = new List<string>();
        if (ocrPages.Count > 0)
        {
            notes.Add($"{Describe(ocrPages)} had no text layer, so Windows read the text from the image (OCR). Check it for mistakes.");
        }

        if (unreadPages.Count > 0)
        {
            notes.Add($"{Describe(unreadPages)} had no text layer, and Windows has no text recognition (OCR) language installed.");
        }

        return new PdfTextResult(pages, notes);
    }

    /// <summary>Opens a PDF with PdfPig; failures become messages the user can act on.</summary>
    public static PdfDocument Open(string path)
    {
        try
        {
            return PdfDocument.Open(path);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw PdfErrors.PasswordProtected(path, ex);
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or InvalidOperationException or ArgumentException or InvalidCastException or IndexOutOfRangeException)
        {
            throw PdfErrors.Unreadable(path, ex);
        }
    }

    /// <summary>"Page 3", "Pages 2 and 5", "Pages 1, 2, and 7", "12 pages".</summary>
    internal static string Describe(IReadOnlyList<int> pages) => pages.Count switch
    {
        1 => $"Page {pages[0]}",
        2 => $"Pages {pages[0]} and {pages[1]}",
        <= 5 => $"Pages {string.Join(", ", pages.Take(pages.Count - 1))}, and {pages[^1]}",
        _ => $"{pages.Count} pages",
    };

    private static List<TextParagraph> Paragraphs(Page page)
    {
        var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        return UnsupervisedReadingOrderDetector.Instance.Get(blocks)
            .OrderBy(block => block.ReadingOrder)
            .Select(block =>
            {
                var letters = block.TextLines.SelectMany(line => line.Words).SelectMany(word => word.Letters).ToList();
                var size = letters.Count > 0 ? letters.Average(letter => letter.PointSize) : DefaultFontSize;
                var bold = letters.Count(letter => IsBold(letter.FontName)) * 2 > letters.Count;
                var text = string.Join(' ', block.TextLines.Select(line => line.Text.Trim()));
                return new TextParagraph(text, size is > 1 and < 200 ? size : DefaultFontSize, bold);
            })
            .Where(paragraph => paragraph.Text.Length > 0)
            .ToList();
    }

    private static bool IsBold(string? fontName) =>
        fontName is not null
        && (fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            || fontName.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase));

    /// <summary>Windows line endings, no trailing spaces, no empty lines at the end.</summary>
    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.TrimEnd());
        return string.Join("\r\n", lines).Trim('\r', '\n');
    }
}
