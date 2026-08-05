using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PRImager.AIAssistant.Services
{
    /// <summary>
    /// Logger forense append-only que registra toda interacción con el modelo de IA.
    /// El archivo de log es evidencia de auditoría para peritajes.
    /// </summary>
    public class ForensicLogger : IDisposable
    {
        private readonly string _logsDirectory;
        private readonly object _lockObj = new object();
        private FileStream? _fileStream;
        private StreamWriter? _writer;
        private string? _currentFilePath;
        private DateTime _currentDate;

        public ForensicLogger(string logsDirectory)
        {
            _logsDirectory = logsDirectory;
            if (!Directory.Exists(_logsDirectory))
            {
                Directory.CreateDirectory(_logsDirectory);
            }
            InitializeLogFile();
        }

        /// <summary>
        /// Obtiene la ruta del archivo de log actual.
        /// </summary>
        public string CurrentLogFilePath
        {
            get
            {
                lock (_lockObj)
                {
                    return _currentFilePath ?? string.Empty;
                }
            }
        }

        private void InitializeLogFile()
        {
            _currentDate = DateTime.Now.Date;
            string dateStr = _currentDate.ToString("yyyy-MM-dd");
            _currentFilePath = Path.Combine(_logsDirectory, $"ai-session-{dateStr}.log");

            // Abrir en modo Append estrictamente, y permitir compartir lectura en tiempo real.
            _fileStream = new FileStream(_currentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_fileStream, Encoding.UTF8) { AutoFlush = true };
        }

        private void EnsureLogFileIsCurrent()
        {
            if (DateTime.Now.Date != _currentDate)
            {
                _writer?.Dispose();
                _fileStream?.Dispose();
                InitializeLogFile();
            }
        }

        private string ComputeSha256Hash(string rawData)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData ?? string.Empty));
                StringBuilder builder = new StringBuilder();
                foreach (byte b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private void WriteLogEntry(string eventType, string userDetails, string contentToHash)
        {
            lock (_lockObj)
            {
                try
                {
                    EnsureLogFileIsCurrent();
                    string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
                    string hash = ComputeSha256Hash(contentToHash);
                    
                    string logLine = $"[{timestamp}] {eventType} hash={hash} {userDetails}";
                    
                    if (_writer != null)
                    {
                        _writer.WriteLine(logLine);
                    }
                }
                catch
                {
                    // Se suprime el error de logging en runtime para evitar crashear la interfaz gráfica,
                    // aunque en la arquitectura final podría emitirse una alerta de auditoría.
                }
            }
        }

        public void LogSessionStart(string caseId)
        {
            string details = $"case={caseId}";
            WriteLogEntry("SESSION_START", details, details);
        }

        public void LogSessionEnd(string caseId)
        {
            string details = $"case={caseId}";
            WriteLogEntry("SESSION_END", details, details);
        }

        public void LogModelLoaded(string modelFileName)
        {
            string details = $"model={modelFileName}";
            WriteLogEntry("MODEL_LOADED", details, details);
        }

        public void LogContextLoaded(int artifactCount, int tokenEstimate)
        {
            string details = $"artifacts={artifactCount} tokens_est={tokenEstimate}";
            WriteLogEntry("CONTEXT_LOADED", details, details);
        }

        public void LogUserQuery(string query)
        {
            string details = $"len={query?.Length ?? 0}";
            WriteLogEntry("USER_QUERY", details, query ?? string.Empty);
        }

        public void LogAIResponse(string response, long elapsedMs)
        {
            string details = $"len={response?.Length ?? 0} time_ms={elapsedMs}";
            WriteLogEntry("AI_RESPONSE", details, response ?? string.Empty);
        }

        public void LogError(string errorMessage)
        {
            string details = $"error=\"{errorMessage}\"";
            WriteLogEntry("ERROR", details, errorMessage ?? string.Empty);
        }

        public void LogCustom(string eventType, string details)
        {
            WriteLogEntry(eventType, details, details ?? string.Empty);
        }

        public void Dispose()
        {
            lock (_lockObj)
            {
                _writer?.Dispose();
                _fileStream?.Dispose();
                
                _writer = null;
                _fileStream = null;
            }
        }
    }
}
