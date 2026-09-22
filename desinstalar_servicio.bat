@echo off
setlocal EnableExtensions

set "SERVICE_NAME=PIT_Boleta_Transacciones"
set "INSTALL_ROOT=%ProgramFiles%\PIT_Boletas_Transacciones"

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo ERROR: Ejecute este desinstalador como Administrador.
    pause
    exit /b 1
)

echo Deteniendo el servicio, si existe...
sc.exe stop "%SERVICE_NAME%" >nul 2>&1

for /L %%N in (1,1,10) do (
    sc.exe query "%SERVICE_NAME%" 2>nul | findstr /I "STOPPED" >nul
    if not errorlevel 1 goto delete_service
    timeout /t 1 /nobreak >nul
)

:delete_service
echo Eliminando el registro del servicio...
sc.exe delete "%SERVICE_NAME%" >nul 2>&1

if exist "%INSTALL_ROOT%" (
    echo Eliminando archivos de "%INSTALL_ROOT%"...
    rmdir /S /Q "%INSTALL_ROOT%"
)

echo.
echo Desinstalacion completada.
pause
exit /b 0
