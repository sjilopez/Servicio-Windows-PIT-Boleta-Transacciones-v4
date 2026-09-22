using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PITBoletaTransacciones.Services;

namespace PITBoletaTransacciones;

public class Worker(
    ILogger<Worker> logger,
    IConfiguration configuration,
    IOcrService ocrService,
    IExternalOcrService externalOcrService,
    IAzureFilesService azureFilesService,
    IPdfCompressionService pdfCompressionService,
    IAzureBlobStorageService azureBlobStorageService) : BackgroundService
{
    private readonly string _inputDirectory = configuration["OcrSettings:InputDirectory"] ?? @"C:\Scans\1_IN";
    private readonly string _validateDirectory = configuration["PipelineSettings:ValidateDirectory"] ?? @"C:\Scans\2_VALIDATE";
    private readonly string _localBackupDirectory = configuration["PipelineSettings:LocalBackupDirectory"] ?? @"C:\Scans\3_LOCAL_BACKUP";
    private readonly string _azureFilesDirectory = configuration["PipelineSettings:AzureFilesDirectory"] ?? @"C:\Scans\4_COPY_AZURE_FILES_STORAGE";
    private readonly string _ocrLocalDirectory = configuration["PipelineSettings:OcrLocalDirectory"] ?? @"C:\Scans\5_OCR_LOCAL";
    private readonly string _externalOcrDirectory = configuration["PipelineSettings:OcrExternalDirectory"] ?? @"C:\Scans\6_OCR_EXTERNO";
    private readonly string _compressDirectory = configuration["AzureFiles:CompressDirectory"] ?? @"C:\Scans\7_COMPRESS";
    private readonly string _blobStorageDirectory = configuration["PdfCompression:OutputDirectory"] ?? @"C:\Scans\8_COPY_AZURE_BLOB_STORAGE";
    private readonly bool _azureBlobStorageEnabled = bool.TryParse(configuration["AzureBlobStorage:Enabled"], out bool azureBlobStorageEnabled) && azureBlobStorageEnabled;
    private readonly int _azureBlobRetryMinutes = GetPositiveInt(configuration["AzureBlobStorage:RetryMinutes"], 5);
    private readonly bool _azureFilesEnabled = bool.TryParse(configuration["AzureFiles:Enabled"], out bool azureFilesEnabled) && azureFilesEnabled;
    private readonly bool _externalOcrEnabled = bool.TryParse(configuration["OCRExterno:Enabled"], out bool externalOcrEnabled) && externalOcrEnabled;
    private readonly string _logsDirectory = configuration["PipelineSettings:LogsDirectory"] ?? @"C:\Scans\Logs";
    private readonly int _scanIntervalSeconds = GetPositiveInt(configuration["PipelineSettings:ScanIntervalSeconds"], 10);
    private readonly int _stabilityChecks = GetPositiveInt(configuration["PipelineSettings:FileStabilityChecks"], 2);
    private readonly int _stabilityDelayMilliseconds = GetPositiveInt(configuration["PipelineSettings:FileStabilityDelayMilliseconds"], 1000);
    private readonly int _fileOpenTimeoutMilliseconds = GetPositiveInt(configuration["PipelineSettings:FileOpenTimeoutMilliseconds"], 3000);
    private readonly int _maxFileReadAttempts = GetPositiveInt(configuration["PipelineSettings:MaxFileReadAttempts"], 3);
    private readonly int _ocrRetryDelaySeconds = GetPositiveInt(configuration["PipelineSettings:OcrRetryDelaySeconds"], 30);
    private readonly int _externalOcrRetryMinutes = GetPositiveInt(configuration["OCRExterno:RetryMinutes"], 2);
    private readonly int _minimumMatches = GetPositiveInt(configuration["ValidationSettings:MinimumMatches"], 4);
    private readonly string[] _validationPhrases = configuration.GetSection("ValidationSettings:Phrases")
        .GetChildren()
        .Select(section => section.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Cast<string>()
        .ToArray();
    private readonly IReadOnlyList<DocumentTypeDefinition> _documentTypes = LoadDocumentTypes(configuration);
    private readonly bool _mySqlEnabled = bool.TryParse(configuration["MySql:Enabled"], out bool enabled) && enabled;
    private readonly string _mySqlConnectionString = BuildMySqlConnectionString(configuration);
    private readonly string _serviceName = configuration["ServiceMetadata:DisplayName"] ?? "PIT - Boleta de Transacciones";
    private readonly string _serviceVersion = configuration["ServiceMetadata:Version"] ?? "4.0.0";
    private bool _ocrReady;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".webp"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Iniciando PIT - Boleta de Transacciones v4.0 (Servicio de Windows)...");
        logger.LogInformation("Directorio base de la aplicación: {BaseDir}", AppContext.BaseDirectory);

        // La etapa 1 solo recibe, identifica y entrega documentos a 2_VALIDATE.
        Directory.CreateDirectory(_inputDirectory);
        Directory.CreateDirectory(_validateDirectory);
        Directory.CreateDirectory(_localBackupDirectory);
        Directory.CreateDirectory(_ocrLocalDirectory);
        Directory.CreateDirectory(_externalOcrDirectory);
        Directory.CreateDirectory(_azureFilesDirectory);
        Directory.CreateDirectory(_compressDirectory);
        Directory.CreateDirectory(_blobStorageDirectory);
        Directory.CreateDirectory(_logsDirectory);

        logger.LogInformation("Carpeta principal de entrada (C:\\Scans\\1_IN): {InDir}", _inputDirectory);
        logger.LogInformation("Carpeta de validación: {ValidateDir}", _validateDirectory);
        logger.LogInformation("Servicio listo y monitoreando archivos entrantes cada {Seconds} segundos.", _scanIntervalSeconds);

        try
        {
            await InitializeOcrWithRetryAsync(stoppingToken);
            _ocrReady = true;
            logger.LogInformation("Modelo OCR cargado y disponible para toda la ejecución del servicio.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo cargar el modelo OCR al iniciar. Se reintentará antes de procesar documentos.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var files = Directory.EnumerateFiles(_inputDirectory)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
                    .ToArray();

                if (files.Length > 0)
                {
                    logger.LogInformation("Se encontraron {Count} documento(s) para procesar.", files.Length);

                    foreach (var file in files)
                    {
                        if (stoppingToken.IsCancellationRequested) break;

                        await MoveToValidationAsync(file, stoppingToken);
                    }
                }

                // Primero intenta vaciar el outbox para que los errores históricos no dependan de nuevos documentos.
                await UploadPendingLogsAsync(stoppingToken);

                var validationFiles = Directory.EnumerateFiles(_validateDirectory)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
                    .ToArray();

                foreach (var file in validationFiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await ProcessValidationFileAsync(file, stoppingToken);
                }

                var azureFiles = Directory.EnumerateFiles(_azureFilesDirectory)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
                    .ToArray();
                foreach (var file in azureFiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await ProcessAzureFilesFileAsync(file, stoppingToken);
                }

                var ocrFiles = Directory.EnumerateFiles(_ocrLocalDirectory)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
                    .ToArray();

                foreach (var file in ocrFiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await ProcessOcrLocalFileAsync(file, stoppingToken);
                }

                var externalOcrFiles = Directory.EnumerateFiles(_externalOcrDirectory, "*.pdf")
                    .ToArray();

                foreach (var file in externalOcrFiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await ProcessExternalOcrFileAsync(file, stoppingToken);
                }

                var compressionFiles = Directory.EnumerateFiles(_compressDirectory, "*.pdf")
                    .ToArray();
                foreach (var file in compressionFiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await ProcessCompressionFileAsync(file, stoppingToken);
                }

                var blobFiles = Directory.EnumerateFiles(_blobStorageDirectory, "*.pdf")
                    .ToArray();
                foreach (var file in blobFiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await ProcessAzureBlobFileAsync(file, stoppingToken);
                }

            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error en el ciclo de escaneo de boletas.");
            }

            // Intervalo de escaneo
            await Task.Delay(TimeSpan.FromSeconds(_scanIntervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessValidationFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
    logger.LogInformation("Etapa 2: procesando {FileName} desde 2_VALIDATE...", fileName);

        try
        {
            if (!await WaitForStableFileAsync(filePath, cancellationToken))
            {
                logger.LogWarning("Etapa 2: archivo no estable o bloqueado {FileName}.", fileName);
                await WritePipelineLogAsync(filePath, "WAITING", "El archivo de 2_VALIDATE todavía está bloqueado.", null, new { StepNumber = 2 }, 2, cancellationToken);
                return;
            }

            string backupPath = Path.Combine(_localBackupDirectory, fileName);
            logger.LogInformation("Etapa 2: copiando respaldo {BackupPath}...", backupPath);
            File.Copy(filePath, backupPath, overwrite: false);
            logger.LogInformation("Etapa 2: respaldo creado {BackupPath}.", backupPath);

            if (!string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                string imageDestination = Path.Combine(_azureFilesDirectory, fileName);
                File.Move(filePath, imageDestination, overwrite: false);
                await WritePipelineLogAsync(imageDestination, "MOVED", "Archivo de imagen respaldado y movido a 4_COPY_AZURE_FILES_STORAGE.", null, new
                {
                    StepNumber = 2,
                    PageCount = 1,
                    BackupPath = backupPath,
                    Destination = imageDestination
                }, 2, cancellationToken);
                return;
            }

            int pageCount = CountPdfPages(filePath);
            logger.LogInformation("Etapa 2: {FileName} contiene {PageCount} página(s).", fileName, pageCount);
            if (pageCount == 1)
            {
                string destination = Path.Combine(_azureFilesDirectory, fileName);
                File.Move(filePath, destination, overwrite: false);
                await WritePipelineLogAsync(destination, "MOVED", "PDF de una página respaldado y movido a 4_COPY_AZURE_FILES_STORAGE.", null, new
                {
                    StepNumber = 2,
                    PageCount = pageCount,
                    BackupPath = backupPath,
                    Destination = destination
                }, 2, cancellationToken);
                return;
            }

            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string pagePath = Path.Combine(_azureFilesDirectory, $"{nameWithoutExtension}_{pageIndex + 1}.pdf");
                SplitPdfPage(filePath, pageIndex, pagePath);
            }

            File.Delete(filePath);
            await WritePipelineLogAsync(backupPath, "SPLIT", "PDF multipágina respaldado, dividido y enviado a 4_COPY_AZURE_FILES_STORAGE.", null, new
            {
                StepNumber = 2,
                PageCount = pageCount,
                BackupPath = backupPath,
                DestinationDirectory = _azureFilesDirectory
            }, 2, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Etapa 2: error con el archivo {FileName}", fileName);
            await WritePipelineLogAsync(filePath, "ERROR", "No se pudo respaldar, contar o dividir el archivo.", ex, new { StepNumber = 2 }, 2, cancellationToken);
        }
    }

    private static int CountPdfPages(string filePath)
    {
        using PdfDocument document = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    private static void SplitPdfPage(string sourcePath, int pageIndex, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            throw new IOException($"El archivo de página ya existe: {destinationPath}");
        }

        using PdfDocument source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
        using PdfDocument pageDocument = new();
        pageDocument.AddPage(source.Pages[pageIndex]);
        pageDocument.Save(destinationPath);
    }

    private async Task ProcessOcrLocalFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        if (!File.Exists(filePath))
        {
            logger.LogInformation("Etapa 5: {FileName} ya no está en 5_OCR_LOCAL; probablemente fue procesado por otra ejecución.", fileName);
            return;
        }

        string fileHash = TryGetFileHash(filePath) ?? throw new InvalidOperationException($"No se pudo calcular el hash de {fileName}.");

        try
        {
            (bool shouldAttempt, bool? completedAsTransactionReceipt) = await GetOcrDecisionAsync(fileHash, cancellationToken);
            if (!shouldAttempt)
            {
                if (completedAsTransactionReceipt.HasValue)
                {
                    string completedDirectory = completedAsTransactionReceipt.Value
                        ? configuration["PipelineSettings:OcrExternalDirectory"] ?? @"C:\Scans\6_OCR_EXTERNO"
                        : configuration["AzureFiles:CompressDirectory"] ?? @"C:\Scans\7_COMPRESS";
                    Directory.CreateDirectory(completedDirectory);
                    string completedDestination = MoveClassifiedFile(filePath, completedDirectory, fileName, fileHash);
                    logger.LogInformation("Etapa 5: {FileName} ya estaba COMPLETED; se movió según su clasificación a {Destination}.", fileName, completedDestination);
                }

                return;
            }

            int attemptNumber = await RegisterOcrAttemptAsync(fileHash, fileName, filePath, cancellationToken);
            logger.LogInformation("Etapa 5: OCR de {FileName}, intento {AttemptNumber}.", fileName, attemptNumber);

            if (!_ocrReady)
            {
                await InitializeOcrWithRetryAsync(cancellationToken);
                _ocrReady = true;
                logger.LogInformation("Modelo OCR cargado después de un reintento y quedará residente hasta reiniciar el servicio.");
            }

            OcrProcessingResult result = await ocrService.ProcessDocumentAsync(filePath, cancellationToken);
            DocumentTypeClassification classification = ClassifyDocument(result.FullText);
            bool isTransactionReceipt = string.Equals(classification.DocumentType, "BOLETA DE TRANSACCIONES", StringComparison.OrdinalIgnoreCase);
            string classificationDirectory = isTransactionReceipt
                ? configuration["PipelineSettings:OcrExternalDirectory"] ?? @"C:\Scans\6_OCR_EXTERNO"
                : configuration["AzureFiles:CompressDirectory"] ?? @"C:\Scans\7_COMPRESS";
            logger.LogInformation(
                "Etapa 5: tipo {DocumentType}, coincidencias {MatchCount}/{MinimumMatches}. Es boleta de transacciones: {IsTransactionReceipt}. Destino: {DestinationDirectory}",
                classification.DocumentType,
                classification.MatchCount,
                classification.MinimumMatches,
                isTransactionReceipt,
                classificationDirectory);
            DateTimeOffset processedAt = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Central America Standard Time");
            Guid resultId = Guid.NewGuid();
            object ocrJson = new
            {
                ResultId = resultId,
                FileName = fileName,
                FilePath = filePath,
                FileHash = fileHash,
                FullText = result.FullText,
                TotalPages = result.TotalPages,
                ElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
                Lines = result.Lines,
                DocumentType = classification.DocumentType,
                MatchedPhrases = classification.MatchedPhrases,
                MatchCount = classification.MatchCount,
                IsTransactionReceipt = isTransactionReceipt,
                ProcessedAt = processedAt.ToString("O", CultureInfo.InvariantCulture)
            };

            await InsertOcrResultAsync(resultId, fileHash, fileName, filePath, result, ocrJson, classification, isTransactionReceipt, processedAt, cancellationToken);

            string destinationDirectory = classificationDirectory;
            Directory.CreateDirectory(destinationDirectory);
            string destination = MoveClassifiedFile(filePath, destinationDirectory, fileName, fileHash);
            await CompleteOcrAttemptAsync(fileHash, cancellationToken);

            await WritePipelineLogAsync(destination, "OCR_CLASSIFIED", "OCR completado, JSON guardado y archivo clasificado.", null, new
            {
                StepNumber = 5,
                AttemptNumber = attemptNumber,
                DocumentType = classification.DocumentType,
                MatchCount = classification.MatchCount,
                MinimumMatches = classification.MinimumMatches,
                IsTransactionReceipt = isTransactionReceipt,
                Destination = destination,
                ResultId = resultId
            }, 5, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            logger.LogInformation("Etapa 5: {FileName} ya fue movido o eliminado por otra ejecución.", fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Etapa 5: error OCR/MySQL para {FileName}.", fileName);
            await FailOcrAttemptAsync(fileHash, fileName, filePath, ex, cancellationToken);
            await WritePipelineLogAsync(filePath, "OCR_ERROR", "OCR o persistencia del resultado falló; el archivo permanece en 5_OCR_LOCAL.", ex, new { StepNumber = 5 }, 5, cancellationToken);
        }
    }

    private static string MoveClassifiedFile(string sourcePath, string destinationDirectory, string fileName, string sourceHash)
    {
        string destinationPath = Path.Combine(destinationDirectory, fileName);
        if (!File.Exists(destinationPath))
        {
            File.Move(sourcePath, destinationPath);
            return destinationPath;
        }

        string? destinationHash = TryGetFileHash(destinationPath);
        if (string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(sourcePath);
            return destinationPath;
        }

        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        int suffix = 1;
        do
        {
            destinationPath = Path.Combine(destinationDirectory, $"{nameWithoutExtension}_{suffix}{extension}");
            suffix++;
        }
        while (File.Exists(destinationPath));

        File.Move(sourcePath, destinationPath);
        return destinationPath;
    }

    private async Task ProcessExternalOcrFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        if (!File.Exists(filePath)) return;

        if (!_externalOcrEnabled)
        {
            logger.LogInformation("OCR externo desactivado. {FileName} permanece en 6_OCR_EXTERNO y no se genera ningún costo.", fileName);
            return;
        }

        string fileHash = TryGetFileHash(filePath) ?? throw new InvalidOperationException($"No se pudo calcular el hash de {fileName}.");
        if (!_mySqlEnabled)
        {
            logger.LogError("OCR externo omitido para {FileName}: MySQL debe estar habilitado para evitar costos duplicados.", fileName);
            return;
        }

        ExternalOcrDecision decision = await GetExternalOcrDecisionAsync(fileHash, cancellationToken);
        if (decision.Success)
        {
            string destination = MoveClassifiedFile(filePath, _compressDirectory, fileName, fileHash);
            await WritePipelineLogAsync(destination, "EXTERNAL_OCR_SKIPPED", "Documento ya procesado previamente. OCR omitido para evitar costo duplicado.", null, new
            {
                StepNumber = 6,
                OcrType = "EXTERNO",
                FileHash = fileHash,
                Destination = destination
            }, 6, cancellationToken);
            logger.LogInformation("OCR externo omitido para {FileName}: ya existe SUCCESS. Movido a {Destination}.", fileName, destination);
            return;
        }

        if (decision.NextAttemptAt.HasValue && decision.NextAttemptAt.Value > GetGuatemalaDateTime())
        {
            return;
        }

        Guid attemptId = Guid.NewGuid();
        int attemptNumber = await CreateExternalOcrAttemptAsync(attemptId, fileHash, fileName, filePath, cancellationToken);
        await UpdateExternalOcrAttemptAsync(attemptId, "PROCESANDO", null, null, cancellationToken);
        Stopwatch stopwatch = Stopwatch.StartNew();
        logger.LogInformation("OCR externo: enviando {FileName}, intento {AttemptNumber}.", fileName, attemptNumber);

        try
        {
            ExternalOcrResponse response = await externalOcrService.ProcessPdfAsync(filePath, cancellationToken);
            stopwatch.Stop();
            await SaveExternalOcrResponseAsync(attemptId, response, cancellationToken);
            string status = response.Success ? "SUCCESS" : "ERROR_API";
            DateTime nextAttempt = GetGuatemalaDateTime().AddMinutes(_externalOcrRetryMinutes);
            await UpdateExternalOcrAttemptAsync(attemptId, status, response.ErrorMessage ?? response.DatabaseError, response.Success ? null : nextAttempt, cancellationToken);

            if (!response.Success)
            {
                logger.LogWarning("OCR externo rechazó {FileName}: {ErrorMessage}. Reintento: {NextAttemptAt}.", fileName, response.ErrorMessage, nextAttempt);
                return;
            }

            string destination = MoveClassifiedFile(filePath, _compressDirectory, fileName, fileHash);
            await WritePipelineLogAsync(destination, "EXTERNAL_OCR_SUCCESS", "OCR externo completado; PDF enviado a 7_COMPRESS.", null, new
            {
                StepNumber = 6,
                OcrType = "EXTERNO",
                AttemptId = attemptId,
                AttemptNumber = attemptNumber,
                ProcessingTime = response.ProcessingTime ?? stopwatch.ElapsedMilliseconds,
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens,
                TotalTokens = response.TotalTokens,
                TotalCost = response.TotalCost,
                DatabaseError = response.DatabaseError,
                Destination = destination
            }, 6, cancellationToken);
            logger.LogInformation("OCR externo SUCCESS para {FileName}. Destino: {Destination}. Costo: {TotalCost}.", fileName, destination, response.TotalCost);
        }
        catch (ExternalOcrException ex)
        {
            stopwatch.Stop();
            if (!string.IsNullOrWhiteSpace(ex.RawJson))
            {
                await SaveExternalOcrErrorResponseAsync(attemptId, ex.RawJson, ex.Message, cancellationToken);
            }

            DateTime nextAttempt = GetGuatemalaDateTime().AddMinutes(_externalOcrRetryMinutes);
            await UpdateExternalOcrAttemptAsync(attemptId, ex.Status, ex.ToString(), nextAttempt, cancellationToken);
            await WritePipelineLogAsync(filePath, ex.Status, "Falló el OCR externo; el PDF permanece en 6_OCR_EXTERNO.", ex, new
            {
                StepNumber = 6,
                OcrType = "EXTERNO",
                AttemptId = attemptId,
                AttemptNumber = attemptNumber,
                NextAttemptAt = nextAttempt
            }, 6, cancellationToken);
            logger.LogError(ex, "OCR externo falló para {FileName}. Reintento: {NextAttemptAt}.", fileName, nextAttempt);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DateTime nextAttempt = GetGuatemalaDateTime().AddMinutes(_externalOcrRetryMinutes);
            await UpdateExternalOcrAttemptAsync(attemptId, "ERROR_PROCESAMIENTO", ex.ToString(), nextAttempt, cancellationToken);
            await WritePipelineLogAsync(filePath, "ERROR_PROCESAMIENTO", "Error interno en OCR externo; el PDF permanece en 6_OCR_EXTERNO.", ex, new { StepNumber = 6, AttemptId = attemptId, AttemptNumber = attemptNumber, NextAttemptAt = nextAttempt }, 6, cancellationToken);
            logger.LogError(ex, "Error procesando OCR externo para {FileName}.", fileName);
        }
    }

    private async Task<ExternalOcrDecision> GetExternalOcrDecisionAsync(string fileHash, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM external_ocr_requests WHERE file_hash = @hash AND status = 'SUCCESS')";
        command.Parameters.AddWithValue("@hash", fileHash);
        bool hasSuccess = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        command.CommandText = "SELECT status, next_attempt_at FROM external_ocr_requests WHERE file_hash = @hash ORDER BY attempt_number DESC LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new ExternalOcrDecision(hasSuccess, null);
        DateTime? nextAttempt = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        return new ExternalOcrDecision(hasSuccess, nextAttempt);
    }

    private async Task<int> CreateExternalOcrAttemptAsync(Guid attemptId, string fileHash, string fileName, string filePath, CancellationToken cancellationToken)
    {
        DateTime now = GetGuatemalaDateTime();
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(attempt_number), 0) + 1 FROM external_ocr_requests WHERE file_hash = @hash";
        command.Parameters.AddWithValue("@hash", fileHash);
        int attemptNumber = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        command.CommandText = """
            INSERT INTO external_ocr_requests
            (attempt_id, file_hash, file_name, file_path, ocr_type, status, attempt_number, host_name, user_name, created_at, started_at, next_attempt_at)
            VALUES (@attempt_id, @hash, @name, @path, 'EXTERNO', 'PENDIENTE', @attempt_number,
                    @host, @user, @now, @now, @now)
            """;
        command.Parameters.AddWithValue("@attempt_id", attemptId.ToString());
        command.Parameters.AddWithValue("@name", fileName);
        command.Parameters.AddWithValue("@path", filePath);
        command.Parameters.AddWithValue("@attempt_number", attemptNumber);
        command.Parameters.AddWithValue("@host", Environment.MachineName);
        command.Parameters.AddWithValue("@user", WindowsSessionUser.GetActiveUser() ?? "UNKNOWN");
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return attemptNumber;
    }

    private async Task UpdateExternalOcrAttemptAsync(Guid attemptId, string status, string? error, DateTime? nextAttemptAt, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE external_ocr_requests SET status = @status, last_error = @error, completed_at = CASE WHEN @status IN ('SUCCESS', 'ERROR_API', 'ERROR_CONEXION', 'ERROR_TIMEOUT', 'ERROR_PROCESAMIENTO') THEN @now ELSE completed_at END, next_attempt_at = @next_attempt WHERE attempt_id = @attempt_id";
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", GetGuatemalaDateTime());
        command.Parameters.AddWithValue("@next_attempt", (object?)nextAttemptAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@attempt_id", attemptId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveExternalOcrResponseAsync(Guid attemptId, ExternalOcrResponse response, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO external_ocr_responses
            (attempt_id, success, request_id, total_pages, input_tokens, output_tokens, total_tokens, input_cost, output_cost, total_cost, processing_time, database_error, error_code, error_message, created_at, completed_at, raw_json)
            VALUES (@attempt_id, @success, @request_id, @total_pages, @input_tokens, @output_tokens, @total_tokens, @input_cost, @output_cost, @total_cost, @processing_time, @database_error, @error_code, @error_message, @created_at, @completed_at, @raw_json);
            """;
        command.Parameters.AddWithValue("@attempt_id", attemptId.ToString());
        command.Parameters.AddWithValue("@success", response.Success);
        command.Parameters.AddWithValue("@request_id", (object?)response.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("@total_pages", (object?)response.TotalPages ?? DBNull.Value);
        command.Parameters.AddWithValue("@input_tokens", (object?)response.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("@output_tokens", (object?)response.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("@total_tokens", (object?)response.TotalTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("@input_cost", (object?)response.InputCost ?? DBNull.Value);
        command.Parameters.AddWithValue("@output_cost", (object?)response.OutputCost ?? DBNull.Value);
        command.Parameters.AddWithValue("@total_cost", (object?)response.TotalCost ?? DBNull.Value);
        command.Parameters.AddWithValue("@processing_time", (object?)response.ProcessingTime ?? DBNull.Value);
        command.Parameters.AddWithValue("@database_error", (object?)response.DatabaseError ?? DBNull.Value);
        command.Parameters.AddWithValue("@error_code", (object?)response.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@error_message", (object?)response.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at", (object?)response.CreatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed_at", (object?)response.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@raw_json", response.RawJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveExternalOcrErrorResponseAsync(Guid attemptId, string rawJson, string errorMessage, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO external_ocr_responses (attempt_id, success, error_message, raw_json) VALUES (@attempt_id, FALSE, @error_message, @raw_json)";
        command.Parameters.AddWithValue("@attempt_id", attemptId.ToString());
        command.Parameters.AddWithValue("@error_message", errorMessage);
        command.Parameters.AddWithValue("@raw_json", rawJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ExternalOcrDecision(bool Success, DateTime? NextAttemptAt);
    private sealed record DocumentTypeDefinition(string Name, string[] Phrases, int MinimumMatches);
    private sealed record DocumentTypeClassification(string DocumentType, IReadOnlyList<string> MatchedPhrases, int MatchCount, int MinimumMatches);

    private async Task ProcessAzureFilesFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        if (!File.Exists(filePath)) return;

        if (!_azureFilesEnabled)
        {
            logger.LogInformation("Azure Files desactivado. {FileName} permanece en 4_COPY_AZURE_FILES_STORAGE.", fileName);
            return;
        }

        string remotePath = BuildAzureRemotePath(fileName);
        try
        {
            AzureFileCopyResult result = await azureFilesService.CopyAsync(filePath, remotePath, cancellationToken);
            string fileHash = TryGetFileHash(filePath) ?? throw new InvalidOperationException($"No se pudo calcular el hash de {fileName}.");
            string destination = Path.Combine(_ocrLocalDirectory, fileName);
            string movedPath = MoveClassifiedFile(filePath, _ocrLocalDirectory, fileName, fileHash);
            await WritePipelineLogAsync(movedPath, "AZURE_FILES_SUCCESS", "Archivo copiado a Azure Files, verificado por tamaño/MD5 y enviado a 5_OCR_LOCAL.", null, new
            {
                StepNumber = 4,
                RemotePath = result.RemotePath,
                FileLength = result.Length,
                LocalMd5 = result.LocalMd5,
                RemoteMd5 = result.RemoteMd5,
                AlreadyExisted = result.AlreadyExisted,
                Destination = destination
            }, 4, cancellationToken);
            logger.LogInformation("Azure Files SUCCESS para {FileName}: {RemotePath}. Enviado a {Destination}.", fileName, result.RemotePath, movedPath);
        }
        catch (Exception ex)
        {
            await WritePipelineLogAsync(filePath, "AZURE_FILES_ERROR", "Falló la copia o verificación en Azure Files; el archivo permanece en 4_COPY_AZURE_FILES_STORAGE.", ex, new
            {
                StepNumber = 4,
                RemotePath = remotePath,
                RetryOnNextCycle = true
            }, 4, cancellationToken);
            logger.LogError(ex, "Azure Files falló para {FileName}; se reintentará en el siguiente ciclo.", fileName);
        }
    }

    private string BuildAzureRemotePath(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string[] parts = baseName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new InvalidDataException($"El nombre no permite construir una ruta Azure Files: {fileName}");
        if (string.Equals(parts[0], "SJAGM", StringComparison.OrdinalIgnoreCase))
        {
            return $"AGENTES/{parts[1]}/{fileName}";
        }

        if (parts[0].StartsWith("SJ", StringComparison.OrdinalIgnoreCase) && parts[0].Length >= 4)
        {
            return $"AGENCIAS/{parts[0].ToUpperInvariant()}/{parts[1]}/{fileName}";
        }

        throw new InvalidDataException($"El prefijo del archivo no tiene una ruta Azure Files configurada: {fileName}");
    }

    private async Task<bool?> GetReceiptClassificationAsync(string fileHash, string fileName, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT is_transaction_receipt
            FROM ocr_results
            WHERE file_hash = @hash OR file_name = @file_name
            ORDER BY (file_hash = @hash) DESC, processed_at DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@hash", fileHash);
        command.Parameters.AddWithValue("@file_name", fileName);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private async Task<bool?> RecoverReceiptClassificationAsync(string filePath, string fileHash, string fileName, CancellationToken cancellationToken)
    {
        try
        {
            if (!_ocrReady)
            {
                await InitializeOcrWithRetryAsync(cancellationToken);
                _ocrReady = true;
            }

            OcrProcessingResult result = await ocrService.ProcessDocumentAsync(filePath, cancellationToken);
            DocumentTypeClassification classification = ClassifyDocument(result.FullText);
            bool isTransactionReceipt = string.Equals(classification.DocumentType, "BOLETA DE TRANSACCIONES", StringComparison.OrdinalIgnoreCase);
            DateTimeOffset processedAt = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Central America Standard Time");
            Guid resultId = Guid.NewGuid();
            object ocrJson = new
            {
                ResultId = resultId,
                FileName = fileName,
                FilePath = filePath,
                FileHash = fileHash,
                FullText = result.FullText,
                TotalPages = result.TotalPages,
                ElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
                Lines = result.Lines,
                DocumentType = classification.DocumentType,
                MatchedPhrases = classification.MatchedPhrases,
                MatchCount = classification.MatchCount,
                IsTransactionReceipt = isTransactionReceipt,
                RecoveredAt = processedAt.ToString("O", CultureInfo.InvariantCulture)
            };

            await InsertOcrResultAsync(resultId, fileHash, fileName, filePath, result, ocrJson, classification, isTransactionReceipt, processedAt, cancellationToken);
            logger.LogInformation("Clasificación OCR recuperada para {FileName}: {DocumentType}, {MatchCount}/{MinimumMatches}, boleta: {IsTransactionReceipt}.", fileName, classification.DocumentType, classification.MatchCount, classification.MinimumMatches, isTransactionReceipt);
            return isTransactionReceipt;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo recuperar la clasificación OCR de {FileName}.", fileName);
            return null;
        }
    }


    private async Task ProcessCompressionFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        if (!File.Exists(filePath)) return;

        try
        {
            string destination = Path.Combine(_blobStorageDirectory, fileName);
            PdfCompressionResult result = await pdfCompressionService.CompressAsync(filePath, destination, cancellationToken);
            if (File.Exists(filePath) && !string.Equals(filePath, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(filePath);
            }

            logger.LogInformation(
                "Compresión completada para {FileName}: {OriginalLength} -> {FinalLength} bytes, {Dpi} DPI, calidad JPEG {JpegQuality}%, reducido: {Compressed}. Destino: {Destination}",
                fileName,
                result.OriginalLength,
                result.FinalLength,
                result.Dpi,
                result.JpegQuality,
                result.Compressed,
                result.OutputPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error comprimiendo {FileName}; el archivo permanece en 7_COMPRESS.", fileName);
        }
    }

    private async Task ProcessAzureBlobFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        if (!File.Exists(filePath)) return;

        if (!_azureBlobStorageEnabled)
        {
            logger.LogInformation("Azure Blob Storage desactivado. {FileName} permanece en 8_COPY_AZURE_BLOB_STORAGE.", fileName);
            return;
        }

        try
        {
            AzureBlobUploadResult result = await azureBlobStorageService.UploadPdfAsync(filePath, cancellationToken);
            File.Delete(filePath);
            await WritePipelineLogAsync(filePath, "AZURE_BLOB_SUCCESS", "PDF copiado y verificado en Azure Blob Storage; archivo local eliminado.", null, new
            {
                StepNumber = 8,
                BlobPath = result.BlobPath,
                FileLength = result.Length,
                LocalMd5 = result.LocalMd5,
                RemoteMd5 = result.RemoteMd5,
                AlreadyExisted = result.AlreadyExisted
            }, 8, cancellationToken);
            logger.LogInformation("Azure Blob Storage SUCCESS para {FileName}: {BlobPath}. Archivo local eliminado.", fileName, result.BlobPath);
        }
        catch (Exception ex)
        {
            await WritePipelineLogAsync(filePath, "AZURE_BLOB_ERROR", "Falló la copia a Azure Blob Storage; el archivo permanece local para reintento.", ex, new
            {
                StepNumber = 8,
                RetryMinutes = _azureBlobRetryMinutes
            }, 8, cancellationToken);
            logger.LogError(ex, "Azure Blob Storage falló para {FileName}; se reintentará en el próximo ciclo.", fileName);
        }
    }

    private async Task InitializeOcrWithRetryAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await ocrService.InitializeAsync(cancellationToken);
                return;
            }
            catch when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(_ocrRetryDelaySeconds * attempt), cancellationToken);
            }
        }

        await ocrService.InitializeAsync(cancellationToken);
    }

    private DocumentTypeClassification ClassifyDocument(string fullText)
    {
        string normalizedText = NormalizeForSearch(fullText);
        foreach (DocumentTypeDefinition documentType in _documentTypes)
        {
            List<string> matchedPhrases = documentType.Phrases
                .Where(phrase => normalizedText.Contains(NormalizeForSearch(phrase), StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchedPhrases.Count >= documentType.MinimumMatches)
            {
                return new DocumentTypeClassification(documentType.Name, matchedPhrases, matchedPhrases.Count, documentType.MinimumMatches);
            }
        }

        return new DocumentTypeClassification("NO CLASIFICADO", [], 0, 0);
    }

    private static IReadOnlyList<DocumentTypeDefinition> LoadDocumentTypes(IConfiguration configuration)
    {
        string[] legacyPhrases = configuration.GetSection("ValidationSettings:Phrases")
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        int legacyMinimum = GetPositiveInt(configuration["ValidationSettings:MinimumMatches"], 4);
        List<DocumentTypeDefinition> definitions = [];

        foreach (IConfigurationSection section in configuration.GetSection("DocumentTypes").GetChildren())
        {
            string name = section["Name"] ?? section.Key;
            string[] phrases = section.GetSection("Phrases")
                .GetChildren()
                .Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
            int minimumMatches = GetPositiveInt(section["MinimumMatches"], legacyMinimum);
            if (!string.IsNullOrWhiteSpace(name) && phrases.Length > 0)
            {
                definitions.Add(new DocumentTypeDefinition(name, phrases, minimumMatches));
            }
        }

        return definitions.Count > 0
            ? definitions
            : [new DocumentTypeDefinition("BOLETA DE TRANSACCIONES", legacyPhrases, legacyMinimum)];
    }

    private static string NormalizeForSearch(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private async Task InsertOcrResultAsync(Guid resultId, string fileHash, string fileName, string filePath, OcrProcessingResult result, object ocrJson, DocumentTypeClassification classification, bool isTransactionReceipt, DateTimeOffset processedAt, CancellationToken cancellationToken)
    {
        if (!_mySqlEnabled) throw new InvalidOperationException("MySQL está deshabilitado para guardar el resultado OCR.");

        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_results
            (result_id, file_hash, file_name, file_path, page_count, line_count, full_text, ocr_json,
             matched_phrases_json, match_count, document_type, is_transaction_receipt, ocr_elapsed_ms, processed_at)
            VALUES
            (@result_id, @file_hash, @file_name, @file_path, @page_count, @line_count, @full_text, @ocr_json,
             @matched_phrases_json, @match_count, @document_type, @is_transaction_receipt, @ocr_elapsed_ms, @processed_at)
            ON DUPLICATE KEY UPDATE
                file_name = VALUES(file_name), file_path = VALUES(file_path), ocr_json = VALUES(ocr_json),
                matched_phrases_json = VALUES(matched_phrases_json), match_count = VALUES(match_count), document_type = VALUES(document_type),
                is_transaction_receipt = VALUES(is_transaction_receipt), processed_at = VALUES(processed_at);
            """;
        command.Parameters.AddWithValue("@result_id", resultId.ToString());
        command.Parameters.AddWithValue("@file_hash", fileHash);
        command.Parameters.AddWithValue("@file_name", fileName);
        command.Parameters.AddWithValue("@file_path", filePath);
        command.Parameters.AddWithValue("@page_count", result.TotalPages);
        command.Parameters.AddWithValue("@line_count", result.Lines.Count);
        command.Parameters.AddWithValue("@full_text", result.FullText);
        command.Parameters.AddWithValue("@ocr_json", JsonSerializer.Serialize(ocrJson));
        command.Parameters.AddWithValue("@matched_phrases_json", JsonSerializer.Serialize(classification.MatchedPhrases));
        command.Parameters.AddWithValue("@match_count", classification.MatchCount);
        command.Parameters.AddWithValue("@document_type", classification.DocumentType);
        command.Parameters.AddWithValue("@is_transaction_receipt", isTransactionReceipt);
        command.Parameters.AddWithValue("@ocr_elapsed_ms", (long)result.Elapsed.TotalMilliseconds);
        command.Parameters.AddWithValue("@processed_at", processedAt.DateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(bool ShouldAttempt, bool? CompletedAsTransactionReceipt)> GetOcrDecisionAsync(string fileHash, CancellationToken cancellationToken)
    {
        if (!_mySqlEnabled) return (true, null);

        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, next_attempt_at FROM ocr_retry_control WHERE file_hash = @file_hash";
        command.Parameters.AddWithValue("@file_hash", fileHash);
        string? status;
        DateTime nextAttemptAt;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return (true, null);
            status = reader.GetString(0);
            nextAttemptAt = reader.GetDateTime(1);
        }

        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return (nextAttemptAt <= GetGuatemalaDateTime(), null);
        }

        command.CommandText = "SELECT is_transaction_receipt FROM ocr_results WHERE file_hash = @file_hash";
        object? classification = await command.ExecuteScalarAsync(cancellationToken);
        return classification is null
            ? (false, null)
            : (false, Convert.ToBoolean(classification, CultureInfo.InvariantCulture));
    }

    private async Task<int> RegisterOcrAttemptAsync(string fileHash, string fileName, string filePath, CancellationToken cancellationToken)
    {
        if (!_mySqlEnabled) return 1;

        DateTime now = GetGuatemalaDateTime();
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ocr_retry_control
            (file_hash, file_name, file_path, first_attempt_at, last_attempt_at, next_attempt_at, attempt_count, status)
            VALUES (@hash, @name, @path, @now, @now, @now, 1, 'PROCESSING')
            ON DUPLICATE KEY UPDATE last_attempt_at = @now, status = 'PROCESSING', attempt_count = attempt_count + 1;
            """;
        command.Parameters.AddWithValue("@hash", fileHash);
        command.Parameters.AddWithValue("@name", fileName);
        command.Parameters.AddWithValue("@path", filePath);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "SELECT attempt_count FROM ocr_retry_control WHERE file_hash = @hash;";
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private async Task CompleteOcrAttemptAsync(string fileHash, CancellationToken cancellationToken)
    {
        if (!_mySqlEnabled) return;
        await UpdateOcrRetryAsync(fileHash, "COMPLETED", null, DateTime.UtcNow, cancellationToken);
    }

    private async Task FailOcrAttemptAsync(string fileHash, string fileName, string filePath, Exception exception, CancellationToken cancellationToken)
    {
        if (!_mySqlEnabled) return;
        try
        {
            DateTime nextAttempt = GetGuatemalaDateTime().AddSeconds(_ocrRetryDelaySeconds);
            await UpdateOcrRetryAsync(fileHash, "FAILED", exception.ToString(), nextAttempt, cancellationToken, fileName, filePath);
        }
        catch (Exception retryException)
        {
            logger.LogError(retryException, "No se pudo actualizar el control de reintentos OCR para {FileName}.", fileName);
        }
    }

    private async Task UpdateOcrRetryAsync(string fileHash, string status, string? error, DateTime nextAttempt, CancellationToken cancellationToken, string? fileName = null, string? filePath = null)
    {
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ocr_retry_control
            SET status = @status, last_error = @error, next_attempt_at = @next_attempt,
                file_name = COALESCE(@file_name, file_name), file_path = COALESCE(@file_path, file_path)
            WHERE file_hash = @hash;
            """;
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@next_attempt", nextAttempt);
        command.Parameters.AddWithValue("@file_name", (object?)fileName ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_path", (object?)filePath ?? DBNull.Value);
        command.Parameters.AddWithValue("@hash", fileHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTime GetGuatemalaDateTime() => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Central America Standard Time").DateTime;

    private async Task MoveToValidationAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        logger.LogInformation("Etapa 1: esperando archivo estable {FileName}...", fileName);

        try
        {
            if (!await WaitForStableFileAsync(filePath, cancellationToken))
            {
                await WritePipelineLogAsync(filePath, "WAITING", "El archivo todavía está siendo creado o permanece bloqueado.", null, null, 1, cancellationToken);
                return;
            }

            FileInfo sourceInfo = new(filePath);
            DateTimeOffset creationTimeGuatemala = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
                new DateTimeOffset(sourceInfo.CreationTimeUtc, TimeSpan.Zero),
                "Central America Standard Time");
            string agency = GetAgency(Environment.MachineName);
            string user = WindowsSessionUser.GetActiveUser() ?? "UNKNOWN";
            string fileNameWithoutExtension = $"{agency}_{SanitizePart(user)}_{creationTimeGuatemala:yyyyMMdd_HHmmssfff}";
            string destination = Path.Combine(_validateDirectory, fileNameWithoutExtension + Path.GetExtension(filePath));

            File.Move(filePath, destination);

            await WritePipelineLogAsync(destination, "MOVED", "Archivo renombrado y movido a 2_VALIDATE.", null, new
            {
                StepNumber = 1,
                OriginalFileName = fileName,
                NewFileName = Path.GetFileName(destination),
                Agency = agency,
                User = user,
                FileCreationTimeGuatemala = creationTimeGuatemala.ToString("O", CultureInfo.InvariantCulture),
                TimeZone = "Central America Standard Time"
            }, 1, cancellationToken);
            logger.LogInformation("Etapa 1 completada: {Destination}", destination);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Etapa 1: error con el archivo {FileName}", fileName);
            await WritePipelineLogAsync(filePath, "ERROR", "No se pudo renombrar o mover el archivo.", ex, null, 1, cancellationToken);
        }
    }

    private async Task<bool> WaitForStableFileAsync(string filePath, CancellationToken cancellationToken)
    {
        long? previousLength = null;
        DateTime? previousWriteTime = null;
        int stableChecks = 0;

        for (int attempt = 0; attempt < _maxFileReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo info = new(filePath);
                long length = info.Length;
                DateTime writeTime = info.LastWriteTimeUtc;
                if (!await CanOpenFileAsync(filePath, cancellationToken))
                {
                    stableChecks = 0;
                    await Task.Delay(_stabilityDelayMilliseconds, cancellationToken);
                    continue;
                }

                if (previousLength == length && previousWriteTime == writeTime)
                {
                    stableChecks++;
                    if (stableChecks >= _stabilityChecks)
                    {
                        return true;
                    }
                }
                else
                {
                    stableChecks = 1;
                }

                previousLength = length;
                previousWriteTime = writeTime;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            await Task.Delay(_stabilityDelayMilliseconds, cancellationToken);
        }

        return false;
    }

    private async Task<bool> CanOpenFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            Task<bool> openTask = Task.Run(() =>
            {
                using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }, CancellationToken.None);

            Task completedTask = await Task.WhenAny(openTask, Task.Delay(_fileOpenTimeoutMilliseconds, cancellationToken));
            return completedTask == openTask && await openTask;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task WritePipelineLogAsync(string filePath, string status, string message, Exception? exception, object? details, int stepNumber, CancellationToken cancellationToken)
    {
        DateTimeOffset guatemalaNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Central America Standard Time");
        string? ipAddress = GetLocalIpAddress();
        string? fileHash = TryGetFileHash(filePath);
        long? fileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : null;
        Guid eventId = Guid.NewGuid();
        var entry = new
        {
            EventId = eventId,
            StepNumber = stepNumber,
            Status = status,
            Message = message,
            ExceptionType = exception?.GetType().FullName,
            ExceptionMessage = exception?.Message,
            StackTrace = exception?.ToString(),
            HostName = Environment.MachineName,
            UserName = WindowsSessionUser.GetActiveUser(),
            IpAddress = ipAddress,
            ServiceName = _serviceName,
            ServiceVersion = _serviceVersion,
            ProcessId = Environment.ProcessId,
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            FileSize = fileSize,
            FileHash = fileHash,
            AttemptNumber = 1,
            OccurredAt = guatemalaNow.ToString("O", CultureInfo.InvariantCulture),
            Details = details
        };

        try
        {
            if (!_mySqlEnabled)
            {
                throw new InvalidOperationException("La persistencia MySQL está deshabilitada.");
            }

            await InsertLogInMySqlAsync(entry, guatemalaNow, cancellationToken);
            logger.LogInformation("Log de pipeline insertado en MySQL. Evento {EventId}, paso {StepNumber}.", eventId, stepNumber);
        }
        catch (Exception databaseException)
        {
            logger.LogError(databaseException, "No se pudo insertar el log {EventId} en MySQL. Se guardará como JSON local.", eventId);
            Directory.CreateDirectory(_logsDirectory);
            string logPath = Path.Combine(_logsDirectory, $"{guatemalaNow:yyyyMMdd_HHmmss_fff}_{eventId:N}.json");
            var fallbackEntry = new
            {
                entry.EventId,
                entry.StepNumber,
                entry.Status,
                entry.Message,
                entry.ExceptionType,
                entry.ExceptionMessage,
                entry.StackTrace,
                entry.HostName,
                entry.UserName,
                entry.IpAddress,
                entry.ServiceName,
                entry.ServiceVersion,
                entry.ProcessId,
                entry.FileName,
                entry.FilePath,
                entry.FileSize,
                entry.FileHash,
                entry.AttemptNumber,
                entry.OccurredAt,
                entry.Details,
                DatabaseError = databaseException.ToString()
            };
            await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(fallbackEntry, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        }
    }

    private async Task InsertLogInMySqlAsync(dynamic entry, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pipeline_logs
            (event_id, step_number, status, message, exception_type, exception_message, stack_trace,
             host_name, user_name, ip_address, service_name, service_version, process_id, file_name,
             file_path, file_size, file_hash, attempt_number, occurred_at, metadata_json)
            VALUES
            (@event_id, @step_number, @status, @message, @exception_type, @exception_message, @stack_trace,
             @host_name, @user_name, @ip_address, @service_name, @service_version, @process_id, @file_name,
             @file_path, @file_size, @file_hash, @attempt_number, @occurred_at, @metadata_json);
            """;
        command.Parameters.AddWithValue("@event_id", entry.EventId.ToString());
        command.Parameters.AddWithValue("@step_number", entry.StepNumber);
        command.Parameters.AddWithValue("@status", entry.Status);
        command.Parameters.AddWithValue("@message", entry.Message);
        command.Parameters.AddWithValue("@exception_type", (object?)entry.ExceptionType ?? DBNull.Value);
        command.Parameters.AddWithValue("@exception_message", (object?)entry.ExceptionMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@stack_trace", (object?)entry.StackTrace ?? DBNull.Value);
        command.Parameters.AddWithValue("@host_name", entry.HostName);
        command.Parameters.AddWithValue("@user_name", (object?)entry.UserName ?? DBNull.Value);
        command.Parameters.AddWithValue("@ip_address", (object?)entry.IpAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("@service_name", entry.ServiceName);
        command.Parameters.AddWithValue("@service_version", entry.ServiceVersion);
        command.Parameters.AddWithValue("@process_id", entry.ProcessId);
        command.Parameters.AddWithValue("@file_name", (object?)entry.FileName ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_path", (object?)entry.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_size", (object?)entry.FileSize ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_hash", (object?)entry.FileHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@attempt_number", entry.AttemptNumber);
        command.Parameters.AddWithValue("@occurred_at", occurredAt.DateTime);
        command.Parameters.AddWithValue("@metadata_json", entry.Details is null ? DBNull.Value : JsonSerializer.Serialize(entry.Details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UploadPendingLogsAsync(CancellationToken cancellationToken)
    {
        if (!_mySqlEnabled)
        {
            return;
        }

        string[] pendingFiles;
        try
        {
            pendingFiles = Directory.EnumerateFiles(_logsDirectory, "*.json")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (pendingFiles.Length > 0)
            {
                logger.LogInformation("Outbox de logs: {Count} archivo(s) pendiente(s) por sincronizar.", pendingFiles.Length);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo leer la carpeta de logs pendientes {LogsDirectory}.", _logsDirectory);
            return;
        }

        foreach (string pendingFile in pendingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (FileStream stream = File.OpenRead(pendingFile))
                using (JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken))
                {
                    await InsertPendingLogInMySqlAsync(document.RootElement, cancellationToken);
                }

                File.Delete(pendingFile);
                logger.LogInformation("Log pendiente sincronizado y eliminado: {PendingFile}.", pendingFile);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "El log pendiente no tiene JSON válido y se conservará: {PendingFile}.", pendingFile);
            }
            catch (MySqlException ex)
            {
                logger.LogWarning(ex, "MySQL no está disponible para sincronizar logs pendientes. Se reintentará en el próximo ciclo.");
                return;
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "No se pudo leer o eliminar el log pendiente {PendingFile}.", pendingFile);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo interpretar o sincronizar el log pendiente {PendingFile}. Se conservará para revisión.", pendingFile);
            }
        }
    }

    private async Task InsertPendingLogInMySqlAsync(JsonElement entry, CancellationToken cancellationToken)
    {
        DateTimeOffset occurredAt = DateTimeOffset.Parse(GetJsonString(entry, "OccurredAt"));
        await using var connection = new MySqlConnection(_mySqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pipeline_logs
            (event_id, step_number, status, message, exception_type, exception_message, stack_trace,
             host_name, user_name, ip_address, service_name, service_version, process_id, file_name,
             file_path, file_size, file_hash, attempt_number, occurred_at, metadata_json)
            VALUES
            (@event_id, @step_number, @status, @message, @exception_type, @exception_message, @stack_trace,
             @host_name, @user_name, @ip_address, @service_name, @service_version, @process_id, @file_name,
             @file_path, @file_size, @file_hash, @attempt_number, @occurred_at, @metadata_json)
            ON DUPLICATE KEY UPDATE event_id = VALUES(event_id);
            """;
        AddPendingParameter(command, "@event_id", GetJsonString(entry, "EventId"));
        AddPendingParameter(command, "@step_number", GetJsonInt(entry, "StepNumber"));
        AddPendingParameter(command, "@status", GetJsonString(entry, "Status"));
        AddPendingParameter(command, "@message", GetJsonString(entry, "Message"));
        AddPendingParameter(command, "@exception_type", GetJsonNullableString(entry, "ExceptionType"));
        AddPendingParameter(command, "@exception_message", GetJsonNullableString(entry, "ExceptionMessage"));
        AddPendingParameter(command, "@stack_trace", GetJsonNullableString(entry, "StackTrace"));
        AddPendingParameter(command, "@host_name", GetJsonNullableString(entry, "HostName"));
        AddPendingParameter(command, "@user_name", GetJsonNullableString(entry, "UserName"));
        AddPendingParameter(command, "@ip_address", GetJsonNullableString(entry, "IpAddress"));
        AddPendingParameter(command, "@service_name", GetJsonNullableString(entry, "ServiceName"));
        AddPendingParameter(command, "@service_version", GetJsonNullableString(entry, "ServiceVersion"));
        AddPendingParameter(command, "@process_id", GetJsonNullableInt(entry, "ProcessId"));
        AddPendingParameter(command, "@file_name", GetJsonNullableString(entry, "FileName"));
        AddPendingParameter(command, "@file_path", GetJsonNullableString(entry, "FilePath"));
        AddPendingParameter(command, "@file_size", GetJsonNullableLong(entry, "FileSize"));
        AddPendingParameter(command, "@file_hash", GetJsonNullableString(entry, "FileHash"));
        AddPendingParameter(command, "@attempt_number", GetJsonNullableInt(entry, "AttemptNumber"));
        AddPendingParameter(command, "@occurred_at", occurredAt.DateTime);
        AddPendingParameter(command, "@metadata_json", entry.TryGetProperty("Details", out JsonElement details) && details.ValueKind != JsonValueKind.Null ? details.GetRawText() : null);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddPendingParameter(MySqlCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string GetJsonString(JsonElement entry, string property) => entry.GetProperty(property).GetString() ?? throw new InvalidDataException($"Falta el campo {property}.");

    private static string? GetJsonNullableString(JsonElement entry, string property) => entry.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static int GetJsonInt(JsonElement entry, string property) => entry.GetProperty(property).GetInt32();

    private static int? GetJsonNullableInt(JsonElement entry, string property) => entry.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;

    private static long? GetJsonNullableLong(JsonElement entry, string property) => entry.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetInt64() : null;

    private static string BuildMySqlConnectionString(IConfiguration configuration)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = configuration["MySql:Server"],
            Port = uint.TryParse(configuration["MySql:Port"], out uint port) ? port : 3306,
            Database = configuration["MySql:Database"],
            UserID = configuration["MySql:User"],
            Password = configuration["MySql:Password"],
            ConnectionTimeout = (uint)GetPositiveInt(configuration["MySql:ConnectTimeoutSeconds"], 5),
            DefaultCommandTimeout = (uint)GetPositiveInt(configuration["MySql:CommandTimeoutSeconds"], 10),
            SslMode = MySqlSslMode.Preferred
        };
        return builder.ConnectionString;
    }

    private static string? GetLocalIpAddress()
    {
        try
        {
            return Dns.GetHostEntry(Environment.MachineName).AddressList
                .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            using FileStream stream = File.OpenRead(filePath);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    private static string GetAgency(string machineName)
    {
        string normalized = SanitizePart(machineName).ToUpperInvariant();
        return normalized.StartsWith("SJAGM", StringComparison.Ordinal) ? normalized[..5] : normalized[..Math.Min(4, normalized.Length)];
    }

    private static string SanitizePart(string value) => new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray()).ToUpperInvariant();

    private static int GetPositiveInt(string? value, int fallback) => int.TryParse(value, out int result) && result > 0 ? result : fallback;
}
