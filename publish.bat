@echo off
echo ============================================
echo  PR-AI Assistant — Build de Distribución
echo  Patagonia Robot — Claudio Andino
echo ============================================
echo.

dotnet publish PR-AI-Assistant.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Publish

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] La compilación falló.
    pause
    exit /b 1
)

echo.
echo ============================================
echo  Build exitoso.
echo  Salida: Publish\PR-AI-Assistant.exe
echo ============================================
echo.
echo IMPORTANTE: Para distribuir, copiar junto al .exe:
echo   - Carpeta Models\ (con archivos .gguf)
echo   - Carpeta Prompts\ (con system_forense.txt)
echo   - Carpeta Config\ (con model_catalog.json, settings.json)
echo   - Carpeta Tools\ (con llama-server.exe y dependencias)
echo.
pause
