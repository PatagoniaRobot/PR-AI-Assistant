using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Exporta el resumen de una sesión de Modo Investigación.
/// Genera un documento auditable con trazabilidad completa.
/// </summary>
public class SessionExporter
{
    /// <summary>
    /// Exporta la sesión al formato especificado.
    /// Retorna la ruta del archivo generado y su hash SHA-256.
    /// </summary>
    public ExportResult Export(SessionExportData data, string outputPath, ExportFormat format)
    {
        try
        {
            string contentWithoutHash = "";
            string finalContent = "";
            string hashValue = "";

            switch (format)
            {
                case ExportFormat.Txt:
                    contentWithoutHash = GenerateTxtContent(data);
                    hashValue = ComputeSha256(contentWithoutHash);
                    finalContent = contentWithoutHash + $"\nHash SHA-256 del documento: {hashValue}\n══════════════════════════════════════════════════════════\n";
                    break;
                    
                case ExportFormat.Html:
                    contentWithoutHash = GenerateHtmlContent(data);
                    hashValue = ComputeSha256(contentWithoutHash);
                    finalContent = InjectHashIntoHtml(contentWithoutHash, hashValue);
                    break;
                    
                case ExportFormat.Json:
                    contentWithoutHash = GenerateJsonContent(data);
                    hashValue = ComputeSha256(contentWithoutHash);
                    finalContent = InjectHashIntoJson(contentWithoutHash, hashValue);
                    break;
            }

            File.WriteAllText(outputPath, finalContent, Encoding.UTF8);

            return new ExportResult
            {
                Success = true,
                FilePath = outputPath,
                FileHash = hashValue
            };
        }
        catch (Exception ex)
        {
            return new ExportResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private string ComputeSha256(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = sha256.ComputeHash(bytes);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private string GenerateTxtContent(SessionExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════════════════════════════════════════════════════════");
        sb.AppendLine("PR-AI ASSISTANT — REPORTE DE SESIÓN DE INVESTIGACIÓN");
        sb.AppendLine("══════════════════════════════════════════════════════════");
        sb.AppendLine("DATOS DE LA SESIÓN");
        sb.AppendLine("──────────────────");
        sb.AppendLine($"Perito:         {data.Session.PeritoName}");
        sb.AppendLine($"Legajo:         {data.Session.PeritoBadge}");
        sb.AppendLine($"Causa:          {data.Session.CaseNumber}");
        sb.AppendLine($"Juzgado:        {data.Session.Court}");
        sb.AppendLine($"Descripción:    {data.Session.Description}");
        sb.AppendLine($"Inicio:         {data.Session.StartTimeUtc:O}");
        sb.AppendLine($"Fin (export):   {data.ExportTimeUtc:O}");
        sb.AppendLine($"ID de sesión:   {data.Session.SessionId}");
        sb.AppendLine();
        
        sb.AppendLine($"ARTEFACTOS CONSULTADOS ({data.Artifacts.Count})");
        sb.AppendLine("────────────────────────────────");
        foreach (var art in data.Artifacts)
        {
            sb.AppendLine($"{art.FileName} | Tipo: {art.FileType} | Tamaño: {art.FileSize} bytes");
            if (!string.IsNullOrEmpty(art.Notes))
                sb.AppendLine($"  Notas: {art.Notes}");
        }
        sb.AppendLine();
        
        sb.AppendLine($"INTERACCIONES ({data.TotalQueries} consultas, {data.TotalResponses} respuestas)");
        sb.AppendLine("───────────────────────────────────────────────────────────────────────");
        foreach (var msg in data.Messages)
        {
            string roleLabel = msg.Role.ToUpperInvariant() == "USER" ? "PERITO" : "PR-AI ASSISTANT";
            if (msg.Role.ToUpperInvariant() == "SYSTEM") roleLabel = "SISTEMA";
            
            sb.AppendLine($"[{msg.Timestamp:O}] {roleLabel}:");
            if (msg.HasImages)
                sb.AppendLine("[CONTIENE IMÁGENES]");
            sb.AppendLine(msg.Content);
            sb.AppendLine("---");
        }
        sb.AppendLine();
        
        sb.AppendLine("ESTADÍSTICAS");
        sb.AppendLine("────────────");
        sb.AppendLine($"Total de consultas:         {data.TotalQueries}");
        sb.AppendLine($"Total de respuestas:        {data.TotalResponses}");
        sb.AppendLine($"Tokens estimados:           {data.TotalTokensEstimated}");
        sb.AppendLine($"Tiempo total de inferencia: {data.TotalInferenceMs} ms");
        sb.AppendLine();
        
        sb.AppendLine("DECLARACIÓN");
        sb.AppendLine("───────────");
        sb.AppendLine("Este reporte fue generado automáticamente por PR-AI Assistant v1.0.");
        sb.AppendLine("El contenido generado por IA debe ser verificado por el perito.");
        sb.AppendLine($"Modelo utilizado: {data.ModelName} ejecutándose localmente.");
        sb.AppendLine("Ningún dato fue transmitido a servidores externos.");
        sb.AppendLine("PR-AI Assistant © 2026 Patagonia Robot.");
        sb.AppendLine("══════════════════════════════════════════════════════════");
        
        return sb.ToString();
    }

    private string GenerateHtmlContent(SessionExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<title>Reporte de Sesión - PR-AI Assistant</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { background-color: #1A1A2E; color: #E0E0E0; font-family: 'Consolas', monospace; padding: 20px; line-height: 1.6; }");
        sb.AppendLine(".container { max-width: 900px; margin: 0 auto; }");
        sb.AppendLine("h1 { color: #10B981; border-bottom: 2px solid #2A2A4A; padding-bottom: 10px; }");
        sb.AppendLine("h2 { color: #00D4AA; margin-top: 30px; border-bottom: 1px solid #2A2A4A; padding-bottom: 5px; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin-bottom: 20px; background-color: #16213E; }");
        sb.AppendLine("th, td { padding: 10px; border: 1px solid #2A2A4A; text-align: left; }");
        sb.AppendLine("th { color: #10B981; }");
        sb.AppendLine(".bubble { padding: 15px; border-radius: 12px; margin-bottom: 15px; max-width: 80%; }");
        sb.AppendLine(".bubble-user { background-color: #3B82F6; color: white; margin-left: auto; }");
        sb.AppendLine(".bubble-assistant { background-color: #1E1E3A; border: 1px solid #2A2A4A; }");
        sb.AppendLine(".bubble-system { background-color: #4B5563; font-style: italic; margin: 0 auto; text-align: center; }");
        sb.AppendLine(".timestamp { font-size: 0.8em; color: #AAAAAA; margin-bottom: 5px; }");
        sb.AppendLine(".footer-section { margin-top: 40px; padding-top: 20px; border-top: 2px solid #2A2A4A; font-size: 0.9em; color: #AAAAAA; }");
        sb.AppendLine("@media print {");
        sb.AppendLine("  body { background-color: white; color: black; }");
        sb.AppendLine("  h1, h2, th { color: black; border-color: #CCC; }");
        sb.AppendLine("  table { background-color: white; border-color: #CCC; }");
        sb.AppendLine("  th, td { border-color: #CCC; }");
        sb.AppendLine("  .bubble { border: 1px solid #CCC; margin-bottom: 10px; }");
        sb.AppendLine("  .bubble-user { background-color: #EEEEEE; color: black; }");
        sb.AppendLine("  .bubble-assistant { background-color: white; }");
        sb.AppendLine("  .bubble-system { background-color: white; border-style: dashed; }");
        sb.AppendLine("}");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");
        
        sb.AppendLine("<h1>🤖 PR-AI Assistant - Reporte de Sesión</h1>");
        
        sb.AppendLine("<h2>Datos de la Sesión</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine($"<tr><th>Perito</th><td>{data.Session.PeritoName} (Legajo: {data.Session.PeritoBadge})</td></tr>");
        sb.AppendLine($"<tr><th>Causa</th><td>{data.Session.CaseNumber} - {data.Session.Court}</td></tr>");
        sb.AppendLine($"<tr><th>Descripción</th><td>{data.Session.Description}</td></tr>");
        sb.AppendLine($"<tr><th>Inicio</th><td>{data.Session.StartTimeUtc:O}</td></tr>");
        sb.AppendLine($"<tr><th>Exportación</th><td>{data.ExportTimeUtc:O}</td></tr>");
        sb.AppendLine($"<tr><th>ID de Sesión</th><td>{data.Session.SessionId}</td></tr>");
        sb.AppendLine("</table>");
        
        sb.AppendLine($"<h2>Artefactos Consultados ({data.Artifacts.Count})</h2>");
        if (data.Artifacts.Count > 0)
        {
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Nombre</th><th>Tipo</th><th>Tamaño</th><th>Notas</th></tr>");
            foreach (var art in data.Artifacts)
            {
                sb.AppendLine($"<tr><td>{art.FileName}</td><td>{art.FileType}</td><td>{art.FileSize} bytes</td><td>{art.Notes}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        else
        {
            sb.AppendLine("<p>No se consultaron artefactos en esta sesión.</p>");
        }
        
        sb.AppendLine($"<h2>Interacciones ({data.TotalQueries} consultas, {data.TotalResponses} respuestas)</h2>");
        sb.AppendLine("<div class=\"chat-container\">");
        foreach (var msg in data.Messages)
        {
            string bubbleClass = msg.Role.ToLowerInvariant() switch
            {
                "user" => "bubble-user",
                "assistant" => "bubble-assistant",
                _ => "bubble-system"
            };
            string roleLabel = msg.Role.ToUpperInvariant() == "USER" ? "PERITO" : (msg.Role.ToUpperInvariant() == "ASSISTANT" ? "PR-AI" : "SISTEMA");
            
            sb.AppendLine($"<div class=\"bubble {bubbleClass}\">");
            sb.AppendLine($"<div class=\"timestamp\">{msg.Timestamp:O} | {roleLabel}</div>");
            if (msg.HasImages)
                sb.AppendLine("<div class=\"timestamp\">[CONTIENE IMÁGENES]</div>");
            sb.AppendLine($"<div>{System.Net.WebUtility.HtmlEncode(msg.Content).Replace("\n", "<br>")}</div>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");
        
        sb.AppendLine("<div class=\"footer-section\">");
        sb.AppendLine("<h2>Estadísticas y Declaración</h2>");
        sb.AppendLine($"<p>Total de consultas: {data.TotalQueries} | Total de respuestas: {data.TotalResponses} | Tokens estimados: {data.TotalTokensEstimated} | Tiempo de inferencia: {data.TotalInferenceMs} ms</p>");
        sb.AppendLine($"<p>Este reporte fue generado automáticamente por PR-AI Assistant v1.0. El contenido generado por IA debe ser verificado por el perito. Modelo utilizado: {System.Net.WebUtility.HtmlEncode(data.ModelName)} ejecutándose localmente. Ningún dato fue transmitido a servidores externos.</p>");
        sb.AppendLine("<p>PR-AI Assistant © 2026 Patagonia Robot.</p>");
        // El placeholder para el hash lo inyectaremos después de cerrarlo
        sb.AppendLine("<!-- HASH_PLACEHOLDER -->");
        sb.AppendLine("</div>");
        
        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        
        return sb.ToString();
    }

    private string InjectHashIntoHtml(string htmlWithoutHash, string hash)
    {
        return htmlWithoutHash.Replace("<!-- HASH_PLACEHOLDER -->", $"<p><strong>Hash SHA-256 del documento original:</strong> {hash}</p>");
    }

    private string GenerateJsonContent(SessionExportData data)
    {
        var exportObj = new
        {
            prai_version = "1.0",
            export_format = "session_report",
            model = data.ModelName,
            session = new
            {
                id = data.Session.SessionId,
                mode = "investigation",
                perito = new { name = data.Session.PeritoName, badge = data.Session.PeritoBadge },
                @case = new { number = data.Session.CaseNumber, court = data.Session.Court, description = data.Session.Description },
                start_time_utc = data.Session.StartTimeUtc,
                export_time_utc = data.ExportTimeUtc
            },
            artifacts = data.Artifacts.Select(a => new
            {
                file_name = a.FileName,
                file_type = a.FileType,
                file_size = a.FileSize,
                notes = a.Notes
            }),
            messages = data.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                timestamp = m.Timestamp,
                has_images = m.HasImages
            }),
            statistics = new
            {
                total_queries = data.TotalQueries,
                total_responses = data.TotalResponses,
                total_tokens_estimated = data.TotalTokensEstimated,
                total_inference_ms = data.TotalInferenceMs
            }
        };

        return JsonSerializer.Serialize(exportObj, new JsonSerializerOptions { WriteIndented = true });
    }

    private string InjectHashIntoJson(string jsonWithoutHash, string hash)
    {
        var node = JsonNode.Parse(jsonWithoutHash) as JsonObject;
        if (node != null)
        {
            node["hash_sha256"] = hash;
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        return jsonWithoutHash;
    }
}
