# PR-AI Assistant — Guía de Distribución Standalone

**Versión:** 1.0.0
**Autor:** Claudio Andino — Patagonia Robot
**Licencia:** Propietaria (motor llama.cpp: MIT, modelo Gemma 4: Apache 2.0)

---

## Estructura de distribución
PR-AI-Assistant/
├── PR-AI-Assistant.exe    ← Ejecutable principal (self-contained, .NET 8)
├── Config/
│   ├── model_catalog.json ← Catálogo de perfiles de modelo
│   └── settings.json      ← Configuración (se crea automáticamente si no existe)
├── Models/
│   ├── *.gguf             ← Archivos de modelo (Gemma 4)
│   └── mmproj           ← Proyector multimodal (opcional, para visión)
├── Prompts/
│   └── system_forense.txt ← Prompt base forense (fallback)
└── Tools/
    └── llama-server.exe   ← Motor de inferencia (MIT License)

## Requisitos del sistema

- **OS:** Windows 10/11 (x64)
- **RAM mínima:** 8 GB (perfil Rendimiento/CPU)
- **RAM recomendada:** 16 GB (perfil Equilibrado/GPU)
- **GPU (opcional):** NVIDIA con CUDA o AMD con Vulkan para aceleración
- **Disco:** ~5 GB (ejecutable + modelo + dependencias)
- **No requiere:** .NET Runtime instalado (self-contained), conexión a internet, API keys

## Cómo compilar

```bat
publish.bat
```

O manualmente:

```bat
dotnet publish PR-AI-Assistant.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Publish
```

## Notas importantes

- **100% offline** — Ningún dato sale de la máquina del perito.
- **Cadena de custodia** — Todas las sesiones se loguean en `%APPDATA%/PRImager/AIAssistant/Logs/`.
- **Modelo comercializable** — Gemma 4 usa licencia Apache 2.0, compatible con uso comercial.
- **Settings** — Si el archivo `settings.json` no existe en Config/, se crea con valores por defecto en `%APPDATA%/PRImager/AIAssistant/`.
