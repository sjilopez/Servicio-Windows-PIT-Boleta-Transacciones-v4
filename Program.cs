using PITBoletaTransacciones;
using PITBoletaTransacciones.Services;

using var singleInstanceMutex = new Mutex(true, "Local\\PIT_Boleta_Transacciones", out bool isFirstInstance);
if (!isFirstInstance)
{
    Console.Error.WriteLine("PIT - Boleta de Transacciones ya está ejecutándose. Se cerrará esta segunda instancia.");
    Environment.ExitCode = 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Habilitar soporte nativo para Windows Service (services.msc / sc.exe)
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PIT - Boleta de Transacciones";
});

// Registrar PaddleOcrService como Singleton para mantener el modelo PP-OCRv6 cargado en memoria
builder.Services.AddSingleton<IOcrService, PaddleOcrService>();
builder.Services.AddHttpClient<IExternalOcrService, ExternalOcrService>();
builder.Services.AddSingleton<IAzureFilesService, AzureFilesService>();
builder.Services.AddSingleton<IPdfCompressionService, PdfCompressionService>();
builder.Services.AddSingleton<IAzureBlobStorageService, AzureBlobStorageService>();

// Registrar el Worker Service en segundo plano
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
