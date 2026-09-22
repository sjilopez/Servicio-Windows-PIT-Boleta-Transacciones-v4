@echo off
setlocal EnableExtensions

set "SERVICE_NAME=PIT_Boleta_Transacciones"
set "DISPLAY_NAME=PIT - Boleta de Transacciones"
set "INSTALL_ROOT=%ProgramFiles%\PIT_Boletas_Transacciones"
set "INSTALL_APP=%INSTALL_ROOT%\app"
set "SOURCE_APP=%~dp0app"
set "SERVICE_EXE=%INSTALL_APP%\PITBoletaTransacciones.exe"

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo ERROR: Ejecute este instalador como Administrador.
    pause
    exit /b 1
)

if not exist "%SOURCE_APP%\PITBoletaTransacciones.exe" (
    echo ERROR: No se encontro la carpeta app junto al instalador.
    echo Ruta esperada: "%SOURCE_APP%"
    pause
    exit /b 1
)

echo Deteniendo una instalacion anterior, si existe...
sc.exe stop "%SERVICE_NAME%" >nul 2>&1
sc.exe delete "%SERVICE_NAME%" >nul 2>&1

echo Creando directorio de instalacion...
if not exist "%INSTALL_APP%" mkdir "%INSTALL_APP%"

if errorlevel 1 (
    echo ERROR: No se pudo crear "%INSTALL_APP%".
    pause
    exit /b 1
)

echo Copiando archivos...
xcopy "%SOURCE_APP%\*" "%INSTALL_APP%\" /E /I /H /R /Y >nul
if errorlevel 1 (
    echo ERROR: No se pudieron copiar los archivos.
    pause
    exit /b 1
)

echo Registrando el servicio...
sc.exe create "%SERVICE_NAME%" binPath= "\"%SERVICE_EXE%\"" start= auto DisplayName= "%DISPLAY_NAME%"
if errorlevel 1 (
    echo ERROR: No se pudo registrar el servicio.
    pause
    exit /b 1
)

sc.exe description "%SERVICE_NAME%" "Servicio de digitalizacion de Boleta de Transacciones con OCR y almacenamiento Azure."
sc.exe failure "%SERVICE_NAME%" reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul
sc.exe config "%SERVICE_NAME%" start= auto >nul

echo Iniciando el servicio...
sc.exe start "%SERVICE_NAME%"
if errorlevel 1 (
    echo ADVERTENCIA: El servicio fue instalado pero no pudo iniciar.
    echo Revise los eventos de Windows y la politica de seguridad del equipo.
    pause
    exit /b 1
)

echo.
echo Instalacion completada correctamente.
echo Servicio: %SERVICE_NAME%
echo Estado:
sc.exe query "%SERVICE_NAME%"
pause
exit /b 0
