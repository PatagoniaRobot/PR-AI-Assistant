<p align="center">
  <img src="src/PR-AI-Assistant/Assets/logo_256.png" width="128" alt="Logo de PR-AI Assistant">
</p>

<h1 align="center">PR-AI Assistant</h1>

<p align="center">
  Un asistente de inteligencia artificial local para Windows. Tus conversaciones, documentos e imágenes no salen de tu PC.
</p>

<p align="center">
  <a href="README.md">English</a> · <b>Español</b>
</p>

---

PR-AI Assistant corre modelos de lenguaje abiertos en tu propia computadora, sin cuenta,
sin suscripción y sin nube. Nació para el trabajo de informática forense, donde la
evidencia no puede salir de la máquina del perito, y sirve igual como asistente privado
de todos los días.

Se publica **sin modelos**. Elegís los que quieras desde un catálogo integrado, y la
aplicación lee tu PC —placa de video de cualquier marca, RAM, procesador— y te dice qué
versión de cada modelo va a andar bien en ella.

![Una pregunta respondida por un modelo local en PR-AI Assistant](docs/images/chat.png)

## Qué hace

- **Chat con modelos locales.** Cualquier modelo GGUF de HuggingFace. Corre en la placa de
  video (NVIDIA, AMD o Intel) o en el procesador si no hay placa.
- **Recomienda según tu PC.** El catálogo marca la cuantización que conviene a tu equipo,
  y el perfil *Automático* fija el contexto, las capas en la placa y los hilos para cada
  modelo.
- **Modo Investigación.** Para analizar evidencia: identificación del perito, registro de
  cadena de custodia con hora UTC y hash SHA-256, archivos de evidencia cargados como
  contexto y exportación de la sesión en TXT, HTML o JSON.
- **PR Academy.** Un modo aislado para consultas técnicas y legales, sin acceso a
  evidencia.
- **Generación de imágenes** (opcional) con Z-Image Turbo, todo local.
- **Búsqueda web** (opcional) a través de una instancia de
  [SearXNG](https://docs.searxng.org/) que montás vos. El botón aparece recién cuando hay
  una configurada.
- **Español e inglés.** Arranca en el idioma de Windows y se cambia desde la pantalla de
  inicio.

| Pantalla de inicio | Catálogo de modelos |
|---|---|
| ![Pantalla de inicio con el Modo Investigación y PR Academy](docs/images/start-screen.png) | ![Catálogo de modelos marcando la cuantización recomendada para la PC](docs/images/model-catalog.png) |

Las capturas son de la aplicación en inglés; en español se ve igual, con los textos traducidos.

## Cómo empezar

1. Bajá la última versión desde la página de **Releases** y descomprimila donde quieras.
2. Abrí `PR-AI-Assistant.exe`. No hay que instalar nada: .NET viene incluido.
3. La primera vez, la aplicación te lleva al catálogo: buscá un modelo, elegí la versión
   recomendada (la de la estrella) y bajala.
4. Elegí un modo, apretá **Iniciar** y esperá que el indicador se ponga verde.

La guía completa está en **Ayuda**, en la pantalla de inicio, en los dos idiomas.

## Requisitos

|                 | Mínimo                                        | Recomendado                    |
|-----------------|-----------------------------------------------|--------------------------------|
| Sistema         | Windows 10 u 11, 64 bits                      |                                |
| RAM             | 8 GB, con modelos chicos                      | 16 GB o más                    |
| Placa de video  | No hace falta: corre en el procesador, más lento | 8 GB de memoria de video o más |
| Para imágenes   | Una placa de video                            | 12 GB de memoria de video o más |
| Disco           | Alrededor de 0,5 GB, más los modelos que bajes |                                |
| Internet        | Solo para bajar modelos y para la búsqueda web |                                |

## Privacidad

Todo corre en tu PC. La aplicación usa la red solo cuando se lo pedís: para buscar o bajar
modelos de HuggingFace, para buscar en la web a través de tu propia instancia de SearXNG
y para bajar los modelos de imagen.

En el Modo Investigación cada sesión queda registrada en
`%APPDATA%\PRImager\AIAssistant\Logs\` para la cadena de custodia.

## Limitaciones conocidas

- **No se probó en placas AMD ni Intel.** El motor Vulkan que las atiende se probó en
  NVIDIA, donde anda. Si tenés una, cualquier reporte suma mucho.
- En AMD e Intel no se mide la memoria libre de la placa, así que el indicador de memoria
  para imágenes dice «desconocido» (no impide generar).
- Algunos repositorios de HuggingFace piden aceptar una licencia y un token de lectura
  para bajar. La aplicación lo avisa cuando pasa.

## Compilar desde el código

Hace falta el [SDK de .NET 10](https://dotnet.microsoft.com/download).

```bat
dotnet build -c Debug          :: compilación de desarrollo, con todo
publish.bat                    :: edición estándar  -> Publish\
publish.bat forense            :: edición forense   -> Publish-Forense\
```

Los motores no están en el repositorio, por su tamaño. Antes de abrirla, se copian en
`src\PR-AI-Assistant\Tools\`:

- **Chat:** `llama-server.exe` y sus `.dll`, de la release `b8671` de
  [llama.cpp](https://github.com/ggml-org/llama.cpp/releases). La versión CUDA va en
  `Tools\` y la Vulkan en `Tools\vulkan\`. Con una de las dos alcanza; la aplicación elige
  la que corresponde a la placa. Sin `ggml-rpc.dll` ni `rpc-server.exe`.
- **Imágenes (opcional):** `sd-server.exe`, de
  [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp/releases)
  `master-975-95b3090`, en `Tools\sd\bin\` (CUDA) o `Tools\sd\bin-vulkan\`.

La edición forense se compila sin búsqueda web, sin descarga de modelos y sin generación
de imágenes: ese código no está en su binario, no está apagado.

Los detalles de cómo se arma una versión están en
[docs/DISTRIBUCION.md](docs/DISTRIBUCION.md), y los bancos de prueba, que corren contra la
aplicación real, en [Tests/README.md](Tests/README.md).

## Licencia

PR-AI Assistant es software libre bajo la [Licencia Apache 2.0](LICENSE).
© 2026 Claudio Andino — Patagonia Robot.

Los componentes incluidos tienen sus propias licencias, listadas en
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Cada modelo que bajes se rige por su
propia licencia, que el catálogo muestra antes de bajarlo.
