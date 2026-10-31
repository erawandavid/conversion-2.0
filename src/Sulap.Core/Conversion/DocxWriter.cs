using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Sulap.Core.Conversion;

/// <summary>Writes DOCX files with the Open XML SDK: one picture per document, or plain paragraphs.</summary>
internal static class DocxWriter
{
    private const long EmuPerInch = 914400;
    private const long EmuPerTwip = 635;
    private const int MarginTwips = 1440;

    /// <summary>A document with the image on the first page, scaled down to fit inside the margins.</summary>
    public static void WriteImage(Stream stream, EmbeddedImage image, string title)
    {
        using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        AddStyles(main);

        var imagePart = main.AddImagePart(image.IsJpeg ? ImagePartType.Jpeg : ImagePartType.Png);
        using (var data = new MemoryStream(image.Bytes, writable: false))
        {
            imagePart.FeedData(data);
        }

        var (pageWidth, pageHeight) = PageSize();
        var maxWidth = (pageWidth - (2 * MarginTwips)) * EmuPerTwip;
        // A little less than the full height, so the paragraph mark after the picture still fits on the page.
        var maxHeight = (pageHeight - (2 * MarginTwips) - 400) * EmuPerTwip;
        var width = image.Width / image.DpiX * EmuPerInch;
        var height = image.Height / image.DpiY * EmuPerInch;
        var scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));

        var picture = Picture(main.GetIdOfPart(imagePart), (long)(width * scale), (long)(height * scale), title);
        main.Document = new Document(new Body(new Paragraph(new Run(picture)), Section(pageWidth, pageHeight)));
        SetProperties(document, title);
    }

    /// <summary>A document with each page's paragraphs, and a page break between pages.</summary>
    public static void WriteText(Stream stream, IReadOnlyList<PageText> pages, string title)
    {
        using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        AddStyles(main);

        var body = new Body();
        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0)
            {
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            }

            foreach (var paragraph in pages[i].Paragraphs)
            {
                body.Append(TextParagraph(paragraph));
            }
        }

        var (pageWidth, pageHeight) = PageSize();
        body.Append(Section(pageWidth, pageHeight));
        main.Document = new Document(body);
        SetProperties(document, title);
    }

    /// <summary>A4 where the metric system is used, US Letter elsewhere, in twips (1/1440 inch).</summary>
    private static (int Width, int Height) PageSize() =>
        RegionInfo.CurrentRegion.IsMetric ? (11906, 16838) : (12240, 15840);

    private static SectionProperties Section(int pageWidth, int pageHeight) => new(
        new PageSize { Width = (UInt32Value)(uint)pageWidth, Height = (UInt32Value)(uint)pageHeight },
        new PageMargin
        {
            Top = MarginTwips,
            Right = (UInt32Value)(uint)MarginTwips,
            Bottom = MarginTwips,
            Left = (UInt32Value)(uint)MarginTwips,
            Header = 720U,
            Footer = 720U,
            Gutter = 0U,
        });

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Calibri", ComplexScript = "Calibri" },
                    new FontSize { Val = "22" },
                    new FontSizeComplexScript { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "160", Line = "259", LineRule = LineSpacingRuleValues.Auto }))));
    }

    private static Paragraph TextParagraph(TextParagraph paragraph)
    {
        var properties = new RunProperties();
        if (paragraph.Bold)
        {
            properties.Append(new Bold());
        }

        var halfPoints = Math.Clamp((int)Math.Round(paragraph.FontSize * 2), 12, 144).ToString(CultureInfo.InvariantCulture);
        properties.Append(new FontSize { Val = halfPoints }, new FontSizeComplexScript { Val = halfPoints });
        return new Paragraph(new Run(properties, new Text(XmlSafe(paragraph.Text)) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static void SetProperties(WordprocessingDocument document, string title)
    {
        document.PackageProperties.Title = title;
        document.PackageProperties.Creator = AppIdentity.Name;
        document.PackageProperties.Created = DateTime.UtcNow;
    }

    /// <summary>PDF text can contain control characters and broken surrogates that XML doesn't allow.</summary>
    private static string XmlSafe(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\t' or '\r' or '\n')
            {
                builder.Append(' ');
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c).Append(text[++i]);
                }
            }
            else if (c >= 0x20 && !char.IsLowSurrogate(c) && c is not ('\uFFFE' or '\uFFFF'))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static Drawing Picture(string relationshipId, long width, long height, string name) => new(
        new DW.Inline(
            new DW.Extent { Cx = width, Cy = height },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = 1U, Name = name },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(
                new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = name },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = width, Cy = height }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
                }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U,
        });
}
