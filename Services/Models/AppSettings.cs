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

        // Inferencia
        public float Temperature { get; set; } = 0.7f;
        public int MaxTokens { get; set; } = 8192;
        public bool EnableThinking { get; set; } = false;

        // Modelo seleccionado (nombre del archivo .gguf)
        public string SelectedModel { get; set; } = "";
        
        // Projector multimodal (opcional para modelos vision)
        public string MmprojFileName { get; set; } = "";

        // UI
        public double WindowWidth { get; set; } = 1200;
        public double WindowHeight { get; set; } = 800;

        // Búsqueda web (E2)
        /// <summary>
        /// API key de Brave Search. Vive solo en la configuración de usuario
        /// (%APPDATA%), nunca en el repositorio.
        /// </summary>
        public string BraveApiKey { get; set; } = "";

        /// <summary>Ventana de frescura por defecto: 0=sin filtro, 1=día, 2=semana, 3=mes.</summary>
        public int WebSearchFreshness { get; set; } = 2;
    }
}
