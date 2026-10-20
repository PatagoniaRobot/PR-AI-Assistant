# PR-AI Assistant — Guía de distribución

**Versión:** 1.0.0
**Autor:** Claudio Andino — Patagonia Robot
**Licencia:** [Apache 2.0](LICENSE), software libre y abierto. Los componentes de terceros
tienen sus propias licencias, todas listadas en [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

---

## Qué se distribuye y qué no

PR-AI Assistant se publica **sin modelos**. Cada usuario baja los que le sirven según su
equipo y lo que necesita hacer, desde la propia aplicación: el catálogo busca en
HuggingFace, lee la PC —placa de cualquier marca, RAM y procesador—, dice cómo correría cada
cuantización en ella y marca la recomendada, y descarga, verifica y registra el archivo
elegido. Al usarlo, el perfil de ejecución **Automático** fija contexto, capas en la placa e
hilos para ese modelo y esa PC.

Las razones:

- **Un solo modelo no le sirve a todos.** El que anda bien en una placa de 16 GB no arranca
  en una de 4, y el que entra en una de 4 desaprovecha una de 16.
- **El paquete pesa megas, no gigas.**
- **Cada usuario acepta la licencia del modelo que elige**, que no siempre es la misma que
  la de la aplicación. La aplicación muestra la licencia declarada antes de bajar.

## Ediciones

Se compilan dos, desde el mismo código:

| | Estándar | Forense (institucional) |
|---|---|---|
| Compilar | `publish.bat` | `publish.bat forense` |
| Salida | `Publish\` | `Publish-Forense\` |
| Chat local con modelos GGUF | sí | sí |
| Modo Investigación (cadena de custodia) | sí | sí |
| Búsqueda web (SearXNG) | sí | **no se compila** |
| Catálogo y descarga de modelos | sí | **no se compila** |
| Generación de imágenes | sí | **no se compila** |

En la edición Forense esas capacidades **no están desactivadas: no existen en el binario**.
Por eso esa edición no puede bajar modelos: los `.gguf` se copian a mano en la carpeta de
modelos, o los provee la institución que la instala.

## Estructura de la carpeta publicada

```
PR-AI-Assistant/
├── PR-AI-Assistant.exe      Ejecutable (self-contained, .NET 8: no requiere instalar .NET)
├── LICENSE                  Apache 2.0
├── THIRD-PARTY-NOTICES.txt  Avisos de terceros: obligatorio incluirlo
├── Config/
│   ├── model_catalog.json   Perfiles de ejecución y modelos sugeridos
│   └── settings.json        Valores por defecto; la configuración del usuario vive en %APPDATA%
├── Prompts/                 Prompts de sistema por modo
├── Help/                    Ayuda en HTML
├── Models/                  Vacía: acá van los modelos que baje o copie el usuario
└── Tools/                   llama-server.exe y sus .dll: motor de chat CUDA, para NVIDIA (MIT)
    ├── vulkan/              El mismo motor en Vulkan: AMD, Intel y también NVIDIA (MIT)
    └── sd/
        ├── bin/             Opcional: sd-server.exe, motor de imágenes CUDA (MIT)
        └── bin-vulkan/      Opcional: el mismo en Vulkan
```

**Cuál motor se usa lo elige la aplicación según la placa**: CUDA si es NVIDIA, Vulkan si
es AMD o Intel, y CPU si no hay placa. Si una de las dos variantes falta, usa la que haya.
Eso permite dos paquetes:

| paquete | chat | imágenes | para quién |
|---|---|---|---|
| completo | CUDA + Vulkan | CUDA + Vulkan | cualquiera; NVIDIA un poco más rápido |
| liviano | solo Vulkan (~90 MB) | solo Vulkan (~28 MB) | cualquiera; ahorra ~1,5 GB de CUDA |

Medido en una RTX 5060 Ti: el chat escribe igual por los dos (59,9 contra 59,3 tok/s) y
lee un texto largo un 17 % más lento por Vulkan; una imagen 1024×1024 tarda lo mismo en
caliente (19,8 contra 20,0 s) y algo más la primera (35 contra 45 s).

`Tools\` y `Tools\vulkan\` llevan **solo** `llama-server.exe` y sus `.dll`. La release de llama.cpp trae 21
ejecutables y además `ggml-rpc.dll` / `rpc-server.exe`, que habilitan inferencia
distribuida por red: no se incluyen, porque contradicen el aislamiento de la edición
Forense.

El motor de imágenes (`Tools\sd\bin\`) no va dentro del paquete por tamaño (~1 GB en su
build CUDA). El usuario lo descomprime ahí o lo elige a mano desde la ventana de modelos
de imagen.

## Requisitos

| | Mínimo | Recomendado |
|---|---|---|
| Sistema | Windows 10/11 x64 | |
| RAM | 8 GB, con modelos chicos y en CPU | 16 GB o más |
| GPU para el chat | no hace falta: corre en CPU, más lento | NVIDIA con 8 GB o más de VRAM |
| GPU para imágenes | **NVIDIA con CUDA, hoy obligatoria** | NVIDIA con 12 GB o más |
| Disco | ~0,5 GB la aplicación, más los modelos que se bajen | |
| Red | solo para bajar modelos y para la búsqueda web | |

**Limitaciones conocidas, a la vista y no escondidas:**

- **Vulkan no se probó todavía en una placa AMD ni Intel.** Se probó en NVIDIA, donde
  anda; la elección de motor y la recomendación están ejercidas con placas descritas.
- **En AMD e Intel no se mide la memoria libre de la placa**: se conoce la total y el
  semáforo de imágenes queda en «desconocido», que no impide generar.
- **Las placas integradas se tratan como sin placa** (menos de 1 GB propio): Vulkan podría
  usarlas con memoria compartida, pero no conviene cargarles un modelo.
- **Algunos repositorios de HuggingFace exigen aceptar la licencia y un token** de lectura
  para descargar. La aplicación lo avisa con un mensaje explícito.

## Privacidad

- **La inferencia es 100 % local.** Las conversaciones, los documentos y las imágenes no
  salen de la máquina.
- **Hay red en tres casos, y solo si el usuario los usa:** buscar o bajar modelos en
  HuggingFace, la búsqueda web a través de una instancia de SearXNG que el usuario
  configura, y bajar el paquete de modelos de imagen. La edición Forense no tiene
  ninguno de los tres.
- **Las sesiones del Modo Investigación quedan registradas** en
  `%APPDATA%\PRImager\AIAssistant\Logs\` para la cadena de custodia.

## Cómo compilar

```bat
publish.bat              :: edición estándar → Publish\
publish.bat forense      :: edición Forense  → Publish-Forense\
```

O a mano:

```bat
dotnet publish PR-AI-Assistant.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Publish
```

`LICENSE` y `THIRD-PARTY-NOTICES.txt` se copian solos desde el `.csproj`. **Antes de
publicar, verificar que estén en la carpeta de salida**: incluirlos es una obligación de
las licencias MIT, Apache 2.0 y del EULA de NVIDIA.

## Antes de publicar una versión

- [ ] `Models\` sale vacía.
- [ ] `Tools\` y `Tools\vulkan\` sin `ggml-rpc.dll` ni `rpc-server.exe`.
- [ ] Las variantes CUDA y Vulkan de cada motor son de **la misma release** (hoy llama.cpp
      `b8671`, stable-diffusion.cpp `master-975-95b3090`).
- [ ] `LICENSE` y `THIRD-PARTY-NOTICES.txt` presentes.
- [ ] Los bancos de `Tests\` pasan contra la compilación que se publica (ver `Tests\README.md`).
- [ ] En la edición Forense, `Tests\GeneracionDeImagenes\verificar-barrera.ps1` da cero fallas.
