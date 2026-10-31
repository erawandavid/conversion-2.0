using System.Runtime.InteropServices.WindowsRuntime;
using ImageMagick;
using Sulap.Core.Formats;
using Sulap.Core.Output;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Sulap.Core.Conversion;

/// <summary>An image encoded for placing inside a PDF or DOCX.</summary>
internal sealed record EmbeddedImage(byte[] Bytes, bool IsJpeg, uint Width, uint Height, double DpiX, double DpiY);

/// <summary>Image reading and writing with Magick.NET, and Windows' own encoder for HEIC.</summary>
internal static class ImageCodec
{
    public static MagickFormat? ToMagickFormat(FileFormat format) => format switch
    {
        FileFormat.Jpeg => MagickFormat.Jpeg,
        FileFormat.Png => MagickFormat.Png,
        FileFormat.WebP => MagickFormat.WebP,
        FileFormat.Heic => MagickFormat.Heic,
        FileFormat.Avif => MagickFormat.Avif,
        FileFormat.Tiff => MagickFormat.Tiff,
        FileFormat.Bmp => MagickFormat.Bmp,
        FileFormat.Gif => MagickFormat.Gif,
        _ => null,
    };

    public static async Task<ConversionOutput> ConvertAsync(
        string source, FileFormat sourceFormat, FileFormat target, string folder, ConversionOptions options, EngineCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var useWindowsHeif = target == FileFormat.Heic && !capabilities.MagickWrites.Contains(FileFormat.Heic);
        using var images = Load(source, sourceFormat);
        var animated = images.Count > 1 && target is FileFormat.Gif or FileFormat.WebP;
        if (animated)
        {
            images.Coalesce();
            foreach (var frame in images)
            {
                Prepare(frame, target, srgbOnly: false);
                ApplyWriteSettings(frame, target, options);
            }
        }
        else
        {
            Prepare(images[0], target, srgbOnly: useWindowsHeif);
            ApplyWriteSettings(images[0], target, options);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = await OutputWriter.WriteFileAsync(
            folder,
            OutputNames.BaseName(source),
            target.Extension(),
            async (stream, token) =>
            {
                if (useWindowsHeif)
                {
                    await WriteWithWindowsHeifAsync(images[0], stream, options.Quality, token);
                }
                else if (animated)
                {
                    images.Write(stream, ToMagickFormat(target)!.Value);
                }
                else
                {
                    images[0].Write(stream, ToMagickFormat(target)!.Value);
                }
            },
            cancellationToken);

        return new ConversionOutput(path, IsFolder: false, new FileInfo(path).Length, Items: 1);
    }

    /// <summary>
    /// Reads every frame of an image file. Failures become messages the user can act on.
    /// </summary>
    public static MagickImageCollection Load(string path, FileFormat format)
    {
        try
        {
            return new MagickImageCollection(path);
        }
        catch (MagickResourceLimitErrorException ex)
        {
            throw new ConversionException($"{Path.GetFileName(path)} is too large to convert.", ex);
        }
        catch (MagickException ex)
        {
            throw new ConversionException(
                $"{Path.GetFileName(path)} couldn't be read. The file may be damaged, or it isn't really a {format.Label()} image.", ex);
        }
    }

    /// <summary>
    /// Gets an image ready to save: the right way up (EXIF orientation applied), in colors the target
    /// can hold, and on white where the target has no transparency.
    /// </summary>
    /// <param name="srgbOnly">The writer can't embed a color profile, so convert the pixels to sRGB.</param>
    public static void Prepare(IMagickImage<byte> image, FileFormat target, bool srgbOnly)
    {
        image.AutoOrient();

        var keepsCmyk = !srgbOnly && target is FileFormat.Jpeg or FileFormat.Tiff;
        var keepsProfile = !srgbOnly && target is FileFormat.Jpeg or FileFormat.Png or FileFormat.WebP or FileFormat.Avif or FileFormat.Tiff or FileFormat.Heic;
        if (image.ColorSpace == ColorSpace.CMYK && !keepsCmyk)
        {
            // Uses the image's own profile when it has one.
            image.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
        }
        else if (!keepsProfile && image.GetColorProfile() is not null)
        {
            image.TransformColorSpace(ColorProfiles.SRGB);
        }

        var flattens = target is FileFormat.Jpeg or FileFormat.Bmp || (target == FileFormat.Heic && srgbOnly);
        if (flattens && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }
    }

    /// <summary>
    /// The image (every page of a TIFF) encoded for a PDF or DOCX. A JPEG that needs no fixing is
    /// embedded as it is, without compressing it again.
    /// </summary>
    public static List<EmbeddedImage> ForEmbedding(string path, FileFormat format)
    {
        if (format == FileFormat.Jpeg)
        {
            using var header = new MagickImage();
            try
            {
                header.Ping(path);
            }
            catch (MagickException ex)
            {
                throw new ConversionException($"{Path.GetFileName(path)} couldn't be read. The file may be damaged, or it isn't really a JPG image.", ex);
            }

            if (header.Orientation is Orientation.TopLeft or Orientation.Undefined && header.ColorSpace != ColorSpace.CMYK)
            {
                var (dpiX, dpiY) = Dpi(header);
                return [new EmbeddedImage(File.ReadAllBytes(path), IsJpeg: true, header.Width, header.Height, dpiX, dpiY)];
            }
        }

        using var images = Load(path, format);
        var frames = format == FileFormat.Tiff ? images.ToList() : [images[0]];
        var embedded = new List<EmbeddedImage>(frames.Count);
        foreach (var frame in frames)
        {
            // Photos stay JPEG (smaller); anything with transparency or sharp edges stays lossless.
            var asJpeg = !frame.HasAlpha && format is FileFormat.Jpeg or FileFormat.WebP or FileFormat.Heic or FileFormat.Avif;
            Prepare(frame, asJpeg ? FileFormat.Jpeg : FileFormat.Png, srgbOnly: true);
            if (asJpeg)
            {
                frame.Quality = 92;
            }

            var bytes = frame.ToByteArray(asJpeg ? MagickFormat.Jpeg : frame.HasAlpha ? MagickFormat.Png32 : MagickFormat.Png24);
            var (dpiX, dpiY) = Dpi(frame);
            embedded.Add(new EmbeddedImage(bytes, asJpeg, frame.Width, frame.Height, dpiX, dpiY));
        }

        return embedded;
    }

    /// <summary>The image's resolution in dots per inch, or 96 when it doesn't say (or says something odd).</summary>
    public static (double X, double Y) Dpi(IMagickImage image)
    {
        var density = image.Density;
        var factor = density.Units == DensityUnit.PixelsPerCentimeter ? 2.54 : 1;
        var x = density.X * factor;
        var y = density.Y * factor;
        return density.Units == DensityUnit.Undefined || x is < 50 or > 2400 || y is < 50 or > 2400 ? (96, 96) : (x, y);
    }

    private static void ApplyWriteSettings(IMagickImage<byte> image, FileFormat target, ConversionOptions options)
    {
        switch (target)
        {
            case FileFormat.Jpeg or FileFormat.WebP or FileFormat.Heic:
                image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
                break;
            case FileFormat.Avif:
                image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
                // AVIF encoding is slow at the default speed; 6 of 9 keeps photos to a few seconds.
                image.Settings.SetDefine("heic:speed", "6");
                break;
            case FileFormat.Tiff:
                // Uncompressed TIFFs of photos get huge. LZW is lossless and every Windows app reads it.
                image.Settings.Compression = CompressionMethod.LZW;
                break;
        }
    }

    /// <summary>
    /// HEIC through Windows' HEIF encoder (the HEIF and HEVC extensions), for builds of Magick.NET
    /// that can't encode HEIC. It doesn't keep EXIF metadata or color profiles; the pixels are sRGB.
    /// </summary>
    private static async Task WriteWithWindowsHeifAsync(IMagickImage<byte> image, Stream output, int quality, CancellationToken cancellationToken)
    {
        var pixels = image.GetPixels().ToByteArray(PixelMapping.BGRA)
            ?? throw new ConversionException("The image's pixels couldn't be read.");
        var (dpiX, dpiY) = Dpi(image);

        using var buffer = new InMemoryRandomAccessStream();
        var encoder = await CreateWindowsHeifEncoderAsync(buffer, quality, cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, image.Width, image.Height, dpiX, dpiY, pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);

        buffer.Seek(0);
        await using var encoded = buffer.AsStreamForRead();
        await encoded.CopyToAsync(output, cancellationToken);
    }

    /// <summary>Windows' HEIF encoder, set up the way conversions use it. The capability check creates one too.</summary>
    internal static Task<BitmapEncoder> CreateWindowsHeifEncoderAsync(IRandomAccessStream stream, int quality, CancellationToken cancellationToken)
    {
        var settings = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, PropertyType.Single),
        };
        return BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream, settings).AsTask(cancellationToken);
    }
}
