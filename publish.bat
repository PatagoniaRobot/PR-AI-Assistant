@echo off
echo ============================================
echo  PR-AI Assistant — Build de Distribución
echo  Patagonia Robot — Claudio Andino
echo ============================================
echo.

rem Uso:  publish.bat            -> build comercial, con busqueda web
rem       publish.bat forense    -> build institucional, SIN codigo de red compilado
set CONFIG=Release
set OUTDIR=Publish
if /I "%~1"=="forense" (
    set CONFIG=Forense
    set OUTDIR=Publish-Forense
)

echo  Configuracion: %CONFIG%
echo  Salida:        %OUTDIR%
echo.

dotnet publish PR-AI-Assistant.csproj -c %CONFIG% -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o %OUTDIR%

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] La compilación falló.
    pause
    exit /b 1
)

echo.
echo ============================================
echo  Build exitoso.
echo  Salida: %OUTDIR%\PR-AI-Assistant.exe
echo ============================================
if /I "%~1"=="forense" (
    echo  Build institucional: sin busqueda web ni AngleSharp.
)
echo.
echo IMPORTANTE: Para distribuir, copiar junto al .exe:
echo   - Carpeta Models\ (con archivos .gguf)
echo   - Carpeta Prompts\ (con system_forense.txt)
echo   - Carpeta Config\ (con model_catalog.json, settings.json)
echo   - Carpeta Tools\ (con llama-server.exe y dependencias)
echo.
pause
