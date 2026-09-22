$projectDir = $PSScriptRoot
$publishDir = Join-Path $projectDir "publish\win-x64"
$distDir = Join-Path $projectDir "dist\PIT_Boletas_Transacciones_Instalador"
$distApp = Join-Path $distDir "app"

Write-Host "Preparando estructura del instalador en: $distDir"

if (Test-Path $distDir) {
    Remove-Item $distDir -Recurse -Force
}

New-Item -ItemType Directory -Path $distApp -Force | Out-Null

if (-not (Test-Path $publishDir)) {
    throw "No existe la publicación: $publishDir. Ejecute dotnet publish primero."
}

Write-Host "Copiando archivos publicados..."
Copy-Item -Path (Join-Path $publishDir "*") -Destination $distApp -Recurse -Force

Write-Host "Copiando scripts de instalacion..."
Copy-Item -Path (Join-Path $projectDir "instalar_servicio.bat") -Destination $distDir -Force
Copy-Item -Path (Join-Path $projectDir "desinstalar_servicio.bat") -Destination $distDir -Force

Write-Host "Limpiando archivos de símbolos (.pdb)..."
Get-ChildItem -Path $distApp -Filter *.pdb -Recurse | Remove-Item -Force

Write-Host "Paquete de aplicación preparado exitosamente."
