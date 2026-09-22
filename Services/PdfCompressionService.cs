using PDFtoImage;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace PITBoletaTransacciones.Services;

public interface IPdfCompressionService
{
    Task<PdfCompressionResult> CompressAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
}

public sealed record PdfCompressionResult(
    string SourcePath,
    string OutputPath,
    long OriginalLength,
    long FinalLength,
    bool Compressed,
    int PageCount,
    int Dpi,
    int JpegQuality);

public sealed class PdfCompressionService(IConfiguration configuration, ILogger<PdfCompressionService> logger) : IPdfCompressionService
{
    public async Task<PdfCompressionResult> CompressAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        int dpi = GetPositiveInt(configuration["PdfCompression:Dpi"], 150);
        int quality = Math.Clamp(GetPositiveInt(configuration["PdfCompression:JpegQuality"], 60), 1, 100);
        long originalLength = new FileInfo(sourcePath).Length;
        string temporaryPath = destinationPath + ".compressing.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        File.Delete(temporaryPath);

        try
        {
            using FileStream sourceStream = File.OpenRead(sourcePath);
            RenderOptions options = new(dpi);
            IReadOnlyList<SKBitmap> pages = Conversion.ToImages(sourceStream, options: options).ToList();
            using var document = new PdfDocument();
            foreach (SKBitmap bitmap in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (bitmap)
                {
                    using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality)
                        ?? throw new InvalidOperationException("No se pudo codificar una página como JPEG.");
                    byte[] jpegBytes = encoded.ToArray();
                    PdfPage page = document.AddPage();
                    page.Width = XUnit.FromPoint(bitmap.Width * 72d / dpi);
                    page.Height = XUnit.FromPoint(bitmap.Height * 72d / dpi);
                    using XGraphics graphics = XGraphics.FromPdfPage(page);
                    using var imageStream = new MemoryStream(jpegBytes, writable: false);
                    using XImage image = XImage.FromStream(imageStream);
                    graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
                }
            }

            document.Save(temporaryPath);
            long compressedLength = new FileInfo(temporaryPath).Length;
            if (compressedLength < originalLength)
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
                logger.LogInformation("Compresión PDF: {SourcePath} reducido de {OriginalLength} a {FinalLength} bytes.", sourcePath, originalLength, compressedLength);
                return new PdfCompressionResult(sourcePath, destinationPath, originalLength, compressedLength, true, pages.Count, dpi, quality);
            }

            File.Delete(temporaryPath);
            if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

            logger.LogInformation("Compresión PDF: el resultado ({FinalLength} bytes) no es menor que el original ({OriginalLength}); se conserva el original.", compressedLength, originalLength);
            return new PdfCompressionResult(sourcePath, destinationPath, originalLength, originalLength, false, pages.Count, dpi, quality);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static int GetPositiveInt(string? value, int fallback) => int.TryParse(value, out int result) && result > 0 ? result : fallback;
}
