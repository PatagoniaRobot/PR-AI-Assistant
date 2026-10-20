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
    echo  Build institucional: sin busqueda web, sin descarga de modelos, sin imagenes.
)
echo.
echo IMPORTANTE: Para distribuir, copiar junto al .exe:
echo   - Carpeta Models\ VACIA: la aplicacion se publica sin modelos, cada
echo     usuario baja los suyos (ver DISTRIBUCION.md)
echo   - Carpeta Prompts\ (con system_forense.txt)
echo   - Carpeta Config\ (con model_catalog.json, settings.json)
echo   - Carpeta Tools\ (con llama-server.exe y dependencias)
echo.
echo TOOLS: la release de llama.cpp trae 21 ejecutables y la aplicacion usa UNO,
echo llama-server.exe. Los otros 20 se sacaron (77,5 MB) junto con ggml-rpc.dll.
echo No volver a copiar la carpeta entera de una release nueva: llevar solo
echo llama-server.exe y las .dll, y NO ggml-rpc.dll ni rpc-server.exe, que dan
echo capacidad de inferencia distribuida por red y contradicen el aislamiento
echo que promete la edicion institucional. Lo mismo vale para Tools\vulkan\,
echo la variante Vulkan del mismo motor: misma release, mismas exclusiones.
echo.
echo LICENCIAS: LICENSE y THIRD-PARTY-NOTICES.txt se copian solos desde el .csproj.
echo Verificar que esten en %OUTDIR%\ antes de entregar: incluirlos es una
echo obligacion de las licencias MIT, Apache 2.0 y del EULA de NVIDIA.
echo.
pause
