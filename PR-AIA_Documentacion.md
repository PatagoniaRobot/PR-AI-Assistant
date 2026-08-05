# PR-AI Assistant - Documentación Técnica y Arquitectura

## 1. Visión General del Proyecto
**PR-AI Assistant** es una aplicación de escritorio desarrollada en **.NET 8 (WPF)** diseñada específicamente para el triaje forense digital. Forma parte de la suite *PRImager Forensic Core*. Su característica principal es la ejecución **100% local** de modelos de Inteligencia Artificial (LLMs y VLMs), garantizando la privacidad de los datos, el cumplimiento estricto de la cadena de custodia y la no alteración de la evidencia digital.

El proyecto actúa como una interfaz especializada que orquesta un motor de inferencia subyacente (`llama.cpp` a través de `llama-server.exe`), proporcionando un entorno seguro, auditable y enfocado en la usabilidad del perito forense.

## 2. Arquitectura de Software

El proyecto sigue el patrón arquitectónico **MVVM (Model-View-ViewModel)**, separando claramente la lógica de negocio y presentación de la interfaz gráfica. 

### 2.1. Capas Principales
1. **Views (Vistas):** Definidas en XAML (`MainWindow`, `ChatView`, `ModeSelectionView`, `InvestigationFormView`). Emplean un diseño moderno oscuro ("Dark Theme") optimizado para lectura prolongada.
2. **ViewModels:** Intermedian entre los servicios y las vistas (`MainViewModel`, `NavigationViewModel`, `ModeSelectionViewModel`, `InvestigationFormViewModel`, `ChatMessageViewModel`). Gestionan el estado de la aplicación, comandos y colecciones observables.
3. **Services (Servicios):** Contienen el núcleo de la lógica de negocio, orquestación del modelo, logging forense, gestión de contexto y lectura de archivos.
4. **Models (Modelos):** Estructuras de datos puras (POCOs/Records) para configuración, estado de sesión, perfiles de IA y formatos de exportación.

### 2.2. Flujo de Ejecución y Orquestación de IA
La orquestación de la Inteligencia Artificial se divide en dos componentes principales: la gestión del proceso del servidor y el cliente de comunicación.

1. **`LlamaServerManager` (Gestión del Proceso):**
   - Se encarga de levantar el proceso hijo `llama-server.exe` (compilación de `llama.cpp`).
   - Pasa los parámetros críticos (modelo GGUF, capas de GPU, hilos, tamaño de contexto, proyector multimodal).
   - Implementa un sistema de *Health Check* mediante un *polling* periódico al endpoint HTTP `/health`.
   - Soporta reinicios automáticos tras 3 fallos consecutivos y usa un **Job Object** de Windows para garantizar que si la aplicación principal colapsa, el proceso del servidor se destruya automáticamente, evitando procesos huérfanos ("zombies") en la memoria de la máquina forense.
   - Analiza el *stdout* del proceso para detectar el backend utilizado (CUDA, CPU, etc.).

2. **`LlamaChatClient` (Cliente API):**
   - Se comunica vía HTTP/REST con el servidor local utilizando un estándar compatible con la API de OpenAI (`/v1/chat/completions`).
   - Soporta **Streaming (SSE)** para renderizar tokens en tiempo real en la UI, mitigando la latencia percibida por el usuario.
   - Maneja el envío de imágenes codificadas en Base64 para modelos de visión (multimodales).

3. **`ChatOrchestrator` (El Cerebro):**
   - Es el servicio central. Conecta el servidor, el cliente, el historial de sesión, el gestor de ventana de contexto y el logger forense.
   - Cada interacción pasa por él: registra el *prompt* del usuario en el log, evalúa el contexto restante, realiza la petición al cliente, procesa el *streaming* y consolida la respuesta.

## 3. Características y Funcionalidades Principales

### 3.1. Modos de Operación
La aplicación impone dos modos de uso estandarizados con distintos *System Prompts*:
- **Modo Investigación:** Requiere completar un formulario de identificación (Nombre y Legajo del Perito, Causa Judicial, Juzgado) y aceptar un descargo legal explícito basado en buenas prácticas (RFC 3227). Se activa el panel de inyección de contexto forense (archivos, evidencias, imágenes). Todo se registra y audita. El *prompt* instruye al modelo a comportarse como un analista estricto.
- **Modo Consulta (Academia PR):** Funciona como un *sandbox* académico para consultas sobre leyes (ej. Ley 26.388), procedimientos técnicos o estructuras de datos. El *System Prompt* instruye explícitamente al modelo a rechazar analizar evidencia real en este modo.

### 3.2. Gestión Inteligente del Contexto (`ContextWindowManager` e `IngestionService`)
Dado que los LLMs locales tienen ventanas de contexto limitadas (ej. 8K o 16K tokens), la aplicación implementa mecanismos de defensa defensiva agresiva:
- **`ContextIngestionService`:** Valida rigurosamente archivos arrastrados o cargados. Rechaza binarios explícitamente (`.exe`, `.dll`) y soporta formatos de texto, CSV, JSON, Hex y múltiples formatos de imagen. Aplica límites de caracteres dinámicos (hasta 50K de texto, 5K de Hex) truncando el exceso.
- **`ContextWindowManager`:** Antes de cada petición de chat, calcula los tokens estimados (heurística rápida de ~4 chars/token). Si el *System Prompt*, el Contexto Forense y el Historial del Chat superan el `CtxSize` disponible, recorta los mensajes más antiguos del historial (preservando siempre las instrucciones base) para evitar un error fatal por exceso de contexto.

### 3.3. Auditoría y Trazabilidad (`ForensicLogger` y `SessionExporter`)
- El **`ForensicLogger`** genera un archivo plano de solo adición (*append-only*) diario (`ai-session-YYYY-MM-DD.log`). Cada interacción y metadato relevante (inicio de sesión, validación de descargo, consulta, respuesta) se registra con su respectivo Timestamp UTC y el **Hash SHA-256** del contenido, vital para garantizar inalterabilidad ante escrutinio judicial.
- El **`SessionExporter`** permite exportar un reporte estructurado y legible de la sesión de investigación terminada en formatos TXT, HTML o JSON. Inyecta el hash SHA-256 del documento final dentro del mismo para facilitar su validación pericial de integridad.

## 4. Modelos de IA: Soporte y Catálogo

El sistema está diseñado para ser agnóstico al modelo, pero la versión actual está optimizada para interactuar con la familia **Gemma 4**.

### 4.1. Catálogo Dinámico (`model_catalog.json`)
La UI no contiene modelos fijos ("hardcodeados"). Emplea un JSON (`model_catalog.json`) que define perfiles de IA abstractos:
- **Rendimiento:** Solo texto, rápido, utiliza CPU si no se detecta GPU.
- **Equilibrado:** Optimo para GPUs estándar (recomendado para la variante `Gemma-4-E2B-it`). Soporta texto e imágenes definiendo el parámetro `mmproj_file_name` (proyector de visión `mmproj-BF16.gguf`).
- **Máxima Calidad:** Requiere hardware de alto nivel para precisión absoluta.

El `ModelProfileService` detecta al arrancar cuáles de los archivos `.gguf` configurados en los perfiles existen físicamente en la carpeta `Models/` y ajusta la interfaz de usuario en consecuencia. 

### 4.2. Multimodalidad (Visión por Computadora)
Mediante la orquestación de VLMs, el perito puede cargar imágenes de evidencia. El sistema se encarga de transformarlas a Base64 y definir el tipo MIME exacto, inyectando el objeto `image_url` en el cuerpo del JSON para el cliente `/chat/completions`.

## 5. Decisiones de Diseño Destacadas

1. **Parser de Markdown Nativo (`MarkdownParser.cs`):** Para evitar dependencias de terceros pesadas y de licenciamiento complejo como renderizadores de HTML/Chromium incrustados, se implementó un parser nativo basado en *Regex*. Éste convierte texto en formato Markdown emitido por la IA directamente a nodos `FlowDocument` estilizados de WPF (burbujas de chat con code blocks, tablas, listas y syntax highlighting básico).
2. **Job Object de Windows (`JobObject.cs`):** Un fallo conocido al empaquetar procesos hijos (`llama.cpp`) es que ante un cierre forzado o *crash* inesperado de la app de escritorio, el ejecutable del servidor queda huérfano (zombie) acaparando VRAM. Mediante llamadas P/Invoke a la API Win32 (`kernel32.dll`), el proceso principal y el de inferencia se entrelazan a nivel del Kernel de Windows; si el padre muere, el Kernel aniquila incondicionalmente a los procesos hijos.
3. **Standalone Deployment (`publish.bat`):** La app está pensada para entornos aislados o "Air-Gapped". El script de publicación genera un binario Self-Contained. El usuario final no necesita conexión a Internet ni frameworks (`.NET Runtime`) previamente instalados en la máquina pericial.

## 6. Oportunidades de Mejora y Deuda Técnica (Para el Equipo de Ingeniería y Partners)

Al observar la arquitectura, existen excelentes oportunidades de estabilización y escalado de producto:

1. **Inyección de Dependencias (DI):**
   - **Situación actual:** Alta acomplación en la inicialización. Los servicios se instancian explícitamente con `new` dentro de `MainWindow.xaml.cs` y se inyectan pasándolos por constructores en cascada hacia el `NavigationViewModel` y secundarios.
   - **Mejora Estratégica:** Implementar un contenedor de dependencias (`Microsoft.Extensions.DependencyInjection`). Esto simplificaría el ciclo de vida de los servicios (Singletons/Transients), mejoraría drásticamente la capacidad de hacer *Unit Testing* mediante mocks y facilitaría el uso del framework `CommunityToolkit.Mvvm`.

2. **Robustez en la Detección y Gestión de GPU (`LlamaServerManager`):**
   - **Situación actual:** Para inferir qué acelerador está usando el sistema (Ej. *CUDA* vs *CPU*), el método `ParseBackendInfo()` examina con strings quemados el flujo del `stdout` (ej. `"ggml_cuda_init"` o `"Device 0:"`).
   - **Mejora Estratégica:** Dado que los logs internos de `llama.cpp` pueden mutar entre builds, es más seguro y profesional interrogar un endpoint de estado estructurado del servidor (como `/props` o `/health`) para determinar el hardware detectado en la capa HTTP.

3. **Mejoras en el `ForensicLogger`:**
   - **Situación actual:** Emplea escritura en archivos de texto plano (`StreamWriter`). Existen bloques `try-catch` que silencian errores de I/O en tiempo de ejecución para evitar caídas de la interfaz gráfica en caso de colisiones.
   - **Mejora Estratégica:** Reemplazar el logger plano por un motor relacional embebido local como **SQLite** (a través de `Entity Framework Core` o `Dapper`), opcionalmente instrumentado por *Serilog*. Esto mantendría la trazabilidad (firmando SHA-256 por registro), pero mitigaría colisiones de escritura concurrente y permitiría a la interfaz gráfica realizar búsquedas históricas avanzadas (ej. recuperar una sesión previa por número de causa judicial).

4. **Incorporación de Generación Aumentada por Recuperación (RAG):**
   - **Situación actual:** La inyección de información de contexto (Logs periciales o volcados en texto) es *naive*: se inserta en su totalidad (con un truncado simple) dentro del *System Prompt*. Documentos forenses masivos simplemente superan los límites.
   - **Mejora Estratégica:** Desarrollar un subsistema de vectorización local. Integrar un *Vector Store* (como ChromaDB, LiteDB vectorial, o Qdrant) localmente, permitiendo que la evidencia textual se fragmente (*chunking*), de modo que ante una pregunta, el asistente recupere e inyecte únicamente los fragmentos semánticamente más relevantes (*RAG workflow*), elevando a nivel empresarial las capacidades del análisis.
