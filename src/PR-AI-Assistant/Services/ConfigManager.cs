using System;
using System.IO;
using System.Text.Json;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services
{
    /// <summary>
    /// Gestiona la configuración persistente de la aplicación desde Config/settings.json.
    /// </summary>
    public class ConfigManager
    {
        public string ConfigFilePath { get; }

        public ConfigManager(string configFilePath)
        {
            ConfigFilePath = configFilePath;
        }

        /// <summary>
        /// Cargar settings.json al inicio y proveer valores por defecto si no existe o faltan campos.
        /// </summary>
        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(ConfigFilePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    PropertyNameCaseInsensitive = true
                };
                var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
                
                if (settings == null)
                    settings = new AppSettings();

                // Validar valores
                settings.Port = Math.Clamp(settings.Port, 1024, 65535);
                settings.Temperature = Math.Clamp(settings.Temperature, 0.0f, 2.0f);
                settings.Threads = Math.Max(1, settings.Threads);

                return settings;
            }
            catch (Exception)
            {
                // En caso de error (json inválido, permisos, etc) se retornan valores por defecto
                return new AppSettings();
            }
        }

        /// <summary>
        /// Guardar cambios a disco de forma robusta.
        /// </summary>
        public void Save(AppSettings settings)
        {
            try
            {
                string? directoryPath = Path.GetDirectoryName(ConfigFilePath);
                if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception)
            {
                // Se suprime el error de guardado para no desestabilizar la app
            }
        }
    }
}
