namespace PITBoletaTransacciones.Services;

public record OcrRegionResult(
    string Text, 
    float Score, 
    int RotationDegrees,
    int PageNumber = 1
);

public record OcrProcessingResult(
    string FullText, 
    IReadOnlyList<OcrRegionResult> Lines, 
    TimeSpan Elapsed,
    int TotalPages = 1
);

public interface IOcrService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<OcrProcessingResult> ProcessDocumentAsync(string filePath, CancellationToken cancellationToken = default);
    OcrProcessingResult ProcessImage(byte[] bgrBytes, int width, int height);
}
