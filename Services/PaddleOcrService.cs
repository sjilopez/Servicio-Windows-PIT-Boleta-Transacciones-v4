using System.Diagnostics;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Small;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Medium;
using SkiaSharp;

namespace PITBoletaTransacciones.Services;

public class PaddleOcrService : IOcrService, IDisposable
{
    private readonly ILogger<PaddleOcrService> _logger;
    private readonly IConfiguration _configuration;
    private PaddleOcrAll? _ocr;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _isDisposed;

    public PaddleOcrService(ILogger<PaddleOcrService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_ocr != null) return;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_ocr != null) return;

            string? configuredModel = _configuration["OcrSettings:Model"]?.Trim();
            bool useTinyModel = string.Equals(configuredModel, "Tiny", StringComparison.OrdinalIgnoreCase);
            bool useSmallModel = string.Equals(configuredModel, "Small", StringComparison.OrdinalIgnoreCase);
            string selectedModel = useTinyModel
                ? "PP-OCRv6-Tiny"
                : useSmallModel
                    ? "PP-OCRv6-Small"
                    : "PP-OCRv6-Medium";

            _logger.LogInformation("Cargando modelo PaddleOCR {Model} (Sdcb.SimdPaddleOCR)...", selectedModel);
            var stopwatch = Stopwatch.StartNew();

            _ocr = useTinyModel
                ? await PaddleOcrAll.LoadAsync(ChineseV6TinyModels.Default)
                : useSmallModel
                    ? await PaddleOcrAll.LoadAsync(ChineseV6SmallModels.Default)
                    : await PaddleOcrAll.LoadAsync(ChineseV6MediumModels.Default);

            stopwatch.Stop();
            _logger.LogInformation("Modelo {Model} cargado correctamente en {ElapsedMs} ms.", selectedModel, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<OcrProcessingResult> ProcessDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("El archivo especificado no existe.", filePath);
        }

        if (_ocr == null)
        {
            await InitializeAsync(cancellationToken);
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".pdf")
        {
            return await ProcessPdfAsync(filePath, cancellationToken);
        }
        else
        {
            return ProcessImageFile(filePath);
        }
    }

    private async Task<OcrProcessingResult> ProcessPdfAsync(string pdfPath, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var allLines = new List<OcrRegionResult>();
        var pageTexts = new List<string>();

        using var stream = File.OpenRead(pdfPath);
        // Renderizar páginas del PDF a imágenes SkiaSharp en memoria
        var pages = PDFtoImage.Conversion.ToImages(stream);

        int pageNumber = 0;
        foreach (var bitmap in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageNumber++;

            using (bitmap)
            {
                byte[] bgrBytes = ConvertSkBitmapToBgr(bitmap);
                var pageResult = ProcessImage(bgrBytes, bitmap.Width, bitmap.Height);

                pageTexts.Add($"[Página {pageNumber}]\n{pageResult.FullText}");
                allLines.AddRange(pageResult.Lines.Select(l => l with { PageNumber = pageNumber }));
            }
        }

        sw.Stop();
        string combinedText = string.Join("\n\n", pageTexts);
        return new OcrProcessingResult(combinedText, allLines, sw.Elapsed, TotalPages: pageNumber);
    }

    private OcrProcessingResult ProcessImageFile(string filePath)
    {
        var sw = Stopwatch.StartNew();

        // SkiaSharp: 100% gratuito (licencia MIT de Google, sin costo ni avisos)
        using var bitmap = SKBitmap.Decode(filePath);
        if (bitmap == null)
        {
            throw new InvalidOperationException($"No se pudo decodificar la imagen: {filePath}");
        }

        byte[] bgrBytes = ConvertSkBitmapToBgr(bitmap);
        var result = ProcessImage(bgrBytes, bitmap.Width, bitmap.Height);
        sw.Stop();

        return result with { Elapsed = sw.Elapsed };
    }

    public OcrProcessingResult ProcessImage(byte[] bgrBytes, int width, int height)
    {
        if (_ocr == null)
        {
            throw new InvalidOperationException("El motor OCR aún no ha sido inicializado. Llame a InitializeAsync primero.");
        }

        var sw = Stopwatch.StartNew();
        PaddleOcrResult ocrResult = _ocr.Run(bgrBytes, width, height);
        sw.Stop();

        var lines = ocrResult.Lines
            .Select(l => new OcrRegionResult(l.Text, l.RecognitionScore, l.AppliedRotationDegrees))
            .ToList();

        return new OcrProcessingResult(ocrResult.Text, lines, sw.Elapsed, TotalPages: 1);
    }

    private static byte[] ConvertSkBitmapToBgr(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        byte[] bgr = new byte[width * height * 3];

        var pixels = bitmap.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            int offset = i * 3;
            bgr[offset] = p.Blue;
            bgr[offset + 1] = p.Green;
            bgr[offset + 2] = p.Red;
        }

        return bgr;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _ocr?.Dispose();
            _semaphore.Dispose();
            _isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
