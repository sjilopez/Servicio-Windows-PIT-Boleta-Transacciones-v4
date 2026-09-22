using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PITBoletaTransacciones.Services;

public interface IExternalOcrService
{
    Task<ExternalOcrResponse> ProcessPdfAsync(string filePath, CancellationToken cancellationToken);
}

public sealed record ExternalOcrResponse(
    bool Success,
    string? RequestId,
    int? TotalPages,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    decimal? InputCost,
    decimal? OutputCost,
    decimal? TotalCost,
    long? ProcessingTime,
    string? DatabaseError,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime? CreatedAt,
    DateTime? CompletedAt,
    string RawJson,
    HttpStatusCode? HttpStatusCode);

public sealed class ExternalOcrException : Exception
{
    public ExternalOcrException(string message, string status, Exception? innerException = null, string? rawJson = null)
        : base(message, innerException)
    {
        Status = status;
        RawJson = rawJson;
    }

    public string Status { get; }
    public string? RawJson { get; }
}

public sealed class ExternalOcrService(HttpClient httpClient, IConfiguration configuration, ILogger<ExternalOcrService> logger) : IExternalOcrService
{
    public async Task<ExternalOcrResponse> ProcessPdfAsync(string filePath, CancellationToken cancellationToken)
    {
        string endpoint = configuration["OCRExterno:Endpoint"]
            ?? throw new InvalidOperationException("Falta la configuración OCRExterno:Endpoint.");
        string apiKey = configuration["OCRExterno:ApiKey"] ?? string.Empty;
        string apiKeyHeader = configuration["OCRExterno:ApiKeyHeader"] ?? "x-api-key";
        string[] fileFieldNames = configuration.GetSection("OCRExterno:FileFieldNames")
            .Get<string[]>()
            ?? new[] { configuration["OCRExterno:FileFieldName"] ?? "file", "files", "pdf", "pdf_file", "document", "archivo" };
        int timeoutSeconds = GetPositiveInt(configuration["OCRExterno:TimeoutSeconds"], 120);

        string? lastMissingFileResponse = null;
        foreach (string fileFieldName in fileFieldNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ExternalOcrCallResult call = await SendPdfAsync(endpoint, apiKey, apiKeyHeader, fileFieldName, filePath, timeoutSeconds, cancellationToken);
            if (!IsMissingFileResponse(call.RawJson))
            {
                return ParseExternalResponse(call.Response, call.RawJson);
            }

            lastMissingFileResponse = call.RawJson;
            logger.LogWarning("El OCR externo no detectó archivo usando el campo multipart {FileFieldName}; se probará el siguiente nombre configurado.", fileFieldName);
        }

        throw new ExternalOcrException("El OCR externo no detectó el PDF en ningún campo multipart configurado.", "ERROR_API", rawJson: lastMissingFileResponse);
    }

    private async Task<ExternalOcrCallResult> SendPdfAsync(string endpoint, string apiKey, string apiKeyHeader, string fileFieldName, string filePath, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation(apiKeyHeader, apiKey);
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        string boundary = "--------------------------" + Guid.NewGuid().ToString("N");
        using var content = new MultipartFormDataContent(boundary);
        content.Headers.ContentType!.Parameters.First(parameter => parameter.Name == "boundary").Value = boundary;
        using var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        fileContent.Headers.ContentLength = fileBytes.Length;
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{fileFieldName}\"",
            FileName = $"\"{Path.GetFileName(filePath)}\""
        };
        content.Add(fileContent);
        request.Content = content;
        request.Headers.ExpectContinue = false;
        request.Headers.TransferEncodingChunked = false;

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalOcrException("El OCR externo excedió el tiempo configurado.", "ERROR_TIMEOUT", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalOcrException("No se pudo conectar con el OCR externo.", "ERROR_CONEXION", ex);
        }

        string rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ExternalOcrCallResult(response, rawJson);
    }

    private ExternalOcrResponse ParseExternalResponse(HttpResponseMessage response, string rawJson)
    {
        if (!response.IsSuccessStatusCode)
        {
            string responseSummary = string.IsNullOrWhiteSpace(rawJson) ? "La API no devolvió cuerpo de respuesta." : rawJson.Length <= 2000 ? rawJson : rawJson[..2000];
            logger.LogError("OCR externo rechazó la solicitud. HTTP {StatusCode}, respuesta: {ResponseBody}", (int)response.StatusCode, responseSummary);
            throw new ExternalOcrException($"El OCR externo respondió HTTP {(int)response.StatusCode}. Respuesta: {responseSummary}", "ERROR_API", rawJson: rawJson);
        }

        try { return ParseResponse(rawJson, response.StatusCode); }
        catch (JsonException ex) { throw new ExternalOcrException("La respuesta del OCR externo no es un JSON válido.", "ERROR_PROCESAMIENTO", ex, rawJson); }
    }

    private static bool IsMissingFileResponse(string rawJson) => rawJson.Contains("No se proporcionó archivo", StringComparison.OrdinalIgnoreCase);

    private sealed record ExternalOcrCallResult(HttpResponseMessage Response, string RawJson);

    private static ExternalOcrResponse ParseResponse(string rawJson, HttpStatusCode statusCode)
    {
        using JsonDocument document = JsonDocument.Parse(rawJson);
        JsonElement root = document.RootElement;
        JsonElement tokens = GetObject(root, "tokens");
        JsonElement costs = GetObject(root, "costs");
        return new ExternalOcrResponse(
            GetBool(root, "success"),
            GetString(root, "requestId"),
            GetInt(root, "pages") ?? GetInt(root, "totalPages"),
            GetInt(tokens, "promptTokens") ?? GetInt(tokens, "inputTokens"),
            GetInt(tokens, "completionTokens") ?? GetInt(tokens, "outputTokens"),
            GetInt(tokens, "totalTokens"),
            GetDecimal(costs, "ocr") ?? GetDecimal(costs, "inputCost"),
            GetDecimal(costs, "chat") ?? GetDecimal(costs, "outputCost"),
            GetDecimal(costs, "total") ?? GetDecimal(costs, "totalCost"),
            GetLong(root, "processingTime"),
            GetString(root, "dbError"),
            GetString(root, "errorCode"),
            GetString(root, "errorMessage") ?? GetString(root, "message"),
            GetDateTime(root, "createdAt"),
            GetDateTime(root, "completedAt"),
            rawJson,
            statusCode);
    }

    private static JsonElement GetObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? GetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static bool GetBool(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static int? GetInt(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : null;
    private static long? GetLong(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : null;
    private static decimal? GetDecimal(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.TryGetDecimal(out decimal result) ? result : null;
    private static DateTime? GetDateTime(JsonElement parent, string name) => DateTime.TryParse(GetString(parent, name), out DateTime result) ? result : null;
    private static int GetPositiveInt(string? value, int fallback) => int.TryParse(value, out int result) && result > 0 ? result : fallback;
}
