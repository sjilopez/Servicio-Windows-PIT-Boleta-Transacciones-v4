$distDir = Join-Path $PSScriptRoot "dist\PIT_Boletas_Transacciones_Instalador"
$source = Join-Path $distDir "*"
$destination = Join-Path $PSScriptRoot "dist\PIT_Boletas_Transacciones_Instalador.zip"

if (Test-Path $destination) {
    Remove-Item $destination -Force
}

Write-Host "Comprimiendo instalador a ZIP (nivel optimo)..."
Compress-Archive -Path $source -DestinationPath $destination -CompressionLevel Optimal

$sizeMB = [math]::Round((Get-Item $destination).Length / 1MB, 2)
Write-Host "ZIP creado exitosamente en: $destination"
Write-Host "Tamano final del archivo ZIP: $sizeMB MB"
