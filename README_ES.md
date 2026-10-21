<p align="center">
  <img src="src/PR-AI-Assistant/Assets/logo_256.png" width="128" alt="Logo de PR-AI Assistant">
</p>

# PR-AI Assistant — Asistente de IA Local

> Asistente de inteligencia artificial de código abierto que corre los modelos de lenguaje enteramente en tu PC con Windows. Tus conversaciones, documentos e imágenes no salen de la máquina.

**Versión:** 1.0.0  
**Licencia:** Apache License 2.0  
**Autor:** Claudio Andino — Patagonia Robot  
**Contacto:** claudio@patagoniarobot.com

[English](README.md) · **Español**

---

![Una pregunta respondida por un modelo local en PR-AI Assistant](docs/images/chat.png)

## ¿Qué es PR-AI Assistant?

PR-AI Assistant corre modelos de lenguaje abiertos en tu propia computadora, sin cuenta,
sin suscripción y sin nube. Nació para el trabajo de informática forense, donde la
evidencia no puede salir de la máquina del perito, y sirve igual como asistente privado
de todos los días.

Se publica **sin modelos**. Elegís los que quieras desde un catálogo integrado, y la
aplicación lee tu PC —placa de video de cualquier marca, RAM, procesador— y te dice qué
versión de cada modelo va a andar bien en ella.

## Características Principales

- **Chat con modelos locales.** Cualquier modelo GGUF de HuggingFace. Corre en la placa de
  video (NVIDIA, AMD o Intel) o en el procesador si no hay placa.
- **Recomienda según tu PC.** El catálogo marca la cuantización que conviene a tu equipo,
  y el perfil *Automático* fija el contexto, las capas en la placa y los hilos para cada
  modelo.
- **Modo Investigación.** Para analizar evidencia: identificación del perito, registro de
  cadena de custodia con hora UTC y hash SHA-256, archivos de evidencia cargados como
  contexto y exportación de la sesión en TXT, HTML o JSON. Cada respuesta lleva el aviso
  de que fue generada por IA y debe verificarla el perito.
- **PR Academy.** Un modo aislado para consultas técnicas y legales, sin acceso a
  evidencia.
- **Generación de imágenes** (opcional) con Z-Image Turbo, todo local.
- **Búsqueda web** (opcional) a través de una instancia de
  [SearXNG](https://docs.searxng.org/) que montás vos. El botón aparece recién cuando hay
  una configurada.
- **Interfaz bilingüe** — español e inglés, según el idioma de Windows, y se cambia desde
  la pantalla de inicio.
- **Totalmente portable** — se descomprime y se usa: sin instalador, sin registro, sin
  permisos de administrador.

| Pantalla de inicio | Catálogo de modelos |
|---|---|
| ![Pantalla de inicio con el Modo Investigación y PR Academy](docs/images/start-screen.png) | ![Catálogo de modelos marcando la cuantización recomendada para la PC](docs/images/model-catalog.png) |

Las capturas son de la aplicación en inglés; en español se ve igual, con los textos traducidos.

## Descargo de Responsabilidad

PR-AI Assistant se ofrece «TAL CUAL», sin garantías de ningún tipo, expresas ni
implícitas, incluidas, entre otras, las de comerciabilidad, idoneidad para un fin
determinado y no infracción.

**Los modelos de lenguaje se equivocan, y afirman cosas falsas con seguridad.** Toda
respuesta tiene que verificarla el usuario antes de confiar en ella, y el perito antes de
incorporarla a cualquier informe o actuación judicial. El usuario es el único responsable
de validar que esta herramienta y los modelos que elija sean adecuados para cada uso,
incluidos los contextos legales, judiciales o regulatorios.

En ningún caso el autor ni los colaboradores serán responsables por reclamos, daños u
otras responsabilidades derivadas del uso de este software. El texto legal completo está
en el archivo LICENSE.

## Instalación

PR-AI Assistant se distribuye como un .zip con la aplicación y sus motores. No hay nada
que instalar.

1. Bajá uno de los dos paquetes desde la
   [página de Releases](https://github.com/PatagoniaRobot/PR-AI-Assistant/releases/latest):

   | Paquete | Tamaño | Para |
   |---|---|---|
   | `PR-AI-Assistant-1.0.0.zip` | 85 MB | **Cualquier PC.** Usa la placa de video de cualquier marca (Vulkan), o el procesador. Ante la duda, este. |
   | `PR-AI-Assistant-1.0.0-NVIDIA.zip` | 604 MB | Placas NVIDIA. Suma el motor CUDA, algo más rápido para leer textos largos. |

2. **Verificá lo que bajaste** contra el `.sha256` publicado al lado: ver más abajo.
3. Descomprimilo donde quieras y abrí `PR-AI-Assistant.exe`. .NET viene incluido.
4. La primera vez, la aplicación te lleva al catálogo: buscá un modelo, elegí la versión
   recomendada (la de la estrella) y bajala.
5. Elegí un modo, apretá **Iniciar** y esperá que el indicador se ponga verde.

Con el motor Vulkan, la primera respuesta puede tardar unos segundos más mientras prepara
la placa de video; pasa una sola vez.

### Verificar la descarga

Cada versión publica un `.sha256` al lado de cada paquete.

```
certutil -hashfile PR-AI-Assistant-1.0.0.zip SHA256
```

Compare el resultado con el contenido de `PR-AI-Assistant-1.0.0.zip.sha256`, o en un equipo
con GNU coreutils: `sha256sum -c PR-AI-Assistant-1.0.0.zip.sha256`.

> El ejecutable **no está firmado con un certificado Authenticode**, así que Windows va a
> mostrar la pantalla *"Windows protegió su PC"* con *"Editor desconocido"* la primera vez.
> Hay que ir a *Más información → Ejecutar de todas formas*. La verificación del SHA-256 es
> lo que realmente acredita que el archivo es el publicado.

## Requisitos del Sistema

|                 | Mínimo                                        | Recomendado                    |
|-----------------|-----------------------------------------------|--------------------------------|
| Sistema         | Windows 10 u 11, 64 bits                      |                                |
| RAM             | 8 GB, con modelos chicos                      | 16 GB o más                    |
| Placa de video  | No hace falta: corre en el procesador, más lento | 8 GB de memoria de video o más |
| Para imágenes   | Una placa de video                            | 12 GB de memoria de video o más |
| Disco           | Alrededor de 1 GB, más los modelos que bajes   |                                |
| Internet        | Solo para bajar modelos y para la búsqueda web |                                |

## Privacidad

Todo corre en tu PC. La aplicación usa la red solo cuando se lo pedís: para buscar o bajar
modelos de HuggingFace, para buscar en la web a través de tu propia instancia de SearXNG
y para bajar los modelos de imagen. No hay telemetría.

En el Modo Investigación cada sesión queda registrada en
`%APPDATA%\PRImager\AIAssistant\Logs\` para la cadena de custodia.

## Limitaciones Conocidas

- **No se probó en placas AMD ni Intel.** El motor Vulkan que las atiende se probó en
  NVIDIA, donde anda. Si tenés una, cualquier reporte suma mucho.
- En AMD e Intel no se mide la memoria libre de la placa, así que el indicador de memoria
  para imágenes dice «desconocido» (no impide generar).
- **El motor de imágenes no viene en los paquetes.** Para generar imágenes, bajá
  `sd-server.exe` de [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp/releases)
  `master-975-95b3090` y descomprimilo en `Tools\sd\bin\` (CUDA) o `Tools\sd\bin-vulkan\`.
- Algunos repositorios de HuggingFace piden aceptar una licencia y un token de lectura
  para bajar. La aplicación lo avisa cuando pasa.

## Compilar desde el Código

PR-AI Assistant se compila con .NET 10 y WPF.

```bat
git clone https://github.com/PatagoniaRobot/PR-AI-Assistant.git
cd PR-AI-Assistant
dotnet build -c Debug
publish.bat
```

`publish.bat` deja la aplicación en `Publish\` y los dos paquetes, con su `.sha256`, en
`Paquetes\`. `publish.bat forense` arma la edición forense en `Publish-Forense\`: se
compila sin búsqueda web, sin descarga de modelos y sin generación de imágenes, así que
ese código no está en su binario, no está apagado.

Los motores no están en el repositorio, por su tamaño. Antes de compilar, se copian en
`src\PR-AI-Assistant\Tools\` el `llama-server.exe` y sus `.dll` de la release `b8671` de
[llama.cpp](https://github.com/ggml-org/llama.cpp/releases): la versión CUDA en `Tools\`
y la Vulkan en `Tools\vulkan\`. Sin `ggml-rpc.dll` ni `rpc-server.exe`.

Los detalles de cómo se arma una versión están en
[docs/DISTRIBUCION.md](docs/DISTRIBUCION.md), y los bancos de prueba, que corren contra la
aplicación real, en [Tests/README.md](Tests/README.md).

## Licencia

PR-AI Assistant se distribuye bajo la **Apache License, Version 2.0**. El texto legal
completo está en el archivo `LICENSE`. Cada modelo que bajes se rige por su propia
licencia, que el catálogo muestra antes de bajarlo.

Los componentes de terceros y sus avisos están en `NOTICE` y `THIRD-PARTY-NOTICES.txt`.

---
© 2026 Patagonia Robot — Tecnología Forense Avanzada  
Desarrollado por Claudio Andino  
Licencia: Apache License 2.0
