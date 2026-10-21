namespace PRImager.AIAssistant.Services.Models
{
    /// <summary>
    /// Clase con todas las configuraciones de la aplicación.
    /// </summary>
    public class AppSettings
    {
        // Rutas
        public string ModelPath { get; set; } = "Models";
        public string PromptsPath { get; set; } = "Prompts";
        public string SessionsPath { get; set; } = "Sessions";
        public string LogsPath { get; set; } = "Logs";
        public string LlamaServerExecutable { get; set; } = "Tools\\llama-server.exe";

        // Servidor llama.cpp
        public int Port { get; set; } = 8787;
        public string Host { get; set; } = "127.0.0.1";
        public int CtxSize { get; set; } = 16384;
        public int GpuLayers { get; set; } = 99;
        public int Threads { get; set; } = 4;

        /// <summary>
        /// Perfil de ejecución elegido. «auto» calcula contexto, capas e hilos para el
        /// modelo y la PC (E5); un settings.json anterior a este campo cae en «auto».
        /// </summary>
        public string ExecutionProfileId { get; set; } = "auto";

        /// <summary>
        /// Formato de la caché de la conversación: «f16» o «q8_0». Lo fija el perfil
        /// Automático cuando comprimirla gana contexto o capas en la placa (E5).
        /// </summary>
        public string KvCacheType { get; set; } = "f16";

        // Inferencia
        public float Temperature { get; set; } = 0.7f;
        public int MaxTokens { get; set; } = 8192;
        public bool EnableThinking { get; set; } = false;

        // Modelo seleccionado (nombre del archivo .gguf)
        public string SelectedModel { get; set; } = "";
        
        // Projector multimodal (opcional para modelos vision)
        public string MmprojFileName { get; set; } = "";

        // UI
        /// <summary>
        /// Idioma: "auto" sigue a Windows, "es" o "en" lo fijan. Se lee al arrancar
        /// (Localization.Loc); cambiarlo pide volver a abrir la aplicación.
        /// </summary>
        public string Language { get; set; } = "auto";

        public double WindowWidth { get; set; } = 1200;
        public double WindowHeight { get; set; } = 800;

        // Búsqueda web (E2)
        /// <summary>
        /// Dirección de la instancia de SearXNG que atiende las búsquedas.
        /// Vacío = búsqueda web sin configurar.
        ///
        /// No es una credencial: es la dirección de un servicio que controla el
        /// usuario. La búsqueda web de esta herramienta no depende de ninguna
        /// cuenta ni de ningún servicio pago.
        /// </summary>
        public string SearxngUrl { get; set; } = "";

        /// <summary>Ventana de frescura por defecto: 0=sin filtro, 1=día, 2=semana, 3=mes.</summary>
        public int WebSearchFreshness { get; set; } = 2;

        // Catálogo de modelos (E3)
        /// <summary>
        /// Token de lectura de HuggingFace. Solo hace falta para repos gated.
        /// Vive en la configuración de usuario, nunca en el repositorio.
        /// </summary>
        public string HuggingFaceToken { get; set; } = "";

        /// <summary>
        /// Carpeta destino de las descargas. Vacío = la carpeta de modelos actual.
        /// </summary>
        public string ModelDownloadPath { get; set; } = "";
    }
}
