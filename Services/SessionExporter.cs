using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PRImager.AIAssistant.Localization;
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
                    finalContent = contentWithoutHash + "\n" + Loc.F("report.txt.hash", hashValue) + "\n══════════════════════════════════════════════════════════\n";
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
        sb.AppendLine(Loc.T("report.txt.title"));
        sb.AppendLine("══════════════════════════════════════════════════════════");
        Heading(sb, Loc.T("report.txt.session"));
        Row(sb, Loc.T("report.perito"), data.Session.PeritoName, 16);
        Row(sb, Loc.T("report.badge"), data.Session.PeritoBadge, 16);
        Row(sb, Loc.T("report.case"), data.Session.CaseNumber, 16);
        Row(sb, Loc.T("report.court"), data.Session.Court, 16);
        Row(sb, Loc.T("report.description"), data.Session.Description, 16);
        Row(sb, Loc.T("report.start"), data.Session.StartTimeUtc.ToString("O"), 16);
        Row(sb, Loc.T("report.end"), data.ExportTimeUtc.ToString("O"), 16);
        Row(sb, Loc.T("report.sessionId"), data.Session.SessionId, 16);
        sb.AppendLine();

        Heading(sb, Loc.F("report.txt.artifacts", data.Artifacts.Count));
        foreach (var art in data.Artifacts)
        {
            sb.AppendLine(Loc.F("report.txt.artifactLine", art.FileName, art.FileType, art.FileSize));
            if (!string.IsNullOrEmpty(art.Notes))
                sb.AppendLine("  " + Loc.F("report.txt.notes", art.Notes));
        }
        sb.AppendLine();

        Heading(sb, Loc.F("report.txt.interactions", data.TotalQueries, data.TotalResponses));
        foreach (var msg in data.Messages)
        {
            string roleLabel = msg.Role.ToUpperInvariant() switch
            {
                "USER" => Loc.T("report.role.user"),
                "SYSTEM" => Loc.T("report.role.system"),
                _ => "PR-AI ASSISTANT"
            };

            sb.AppendLine($"[{msg.Timestamp:O}] {roleLabel}:");
            if (msg.HasImages)
                sb.AppendLine(Loc.T("report.hasImages"));
            sb.AppendLine(msg.Content);
            sb.AppendLine("---");
        }
        sb.AppendLine();

        Heading(sb, Loc.T("report.txt.stats"));
        Row(sb, Loc.T("report.totalQueries"), data.TotalQueries.ToString(), 28);
        Row(sb, Loc.T("report.totalResponses"), data.TotalResponses.ToString(), 28);
        Row(sb, Loc.T("report.tokens"), data.TotalTokensEstimated.ToString(), 28);
        Row(sb, Loc.T("report.inferenceTime"), $"{data.TotalInferenceMs} ms", 28);
        sb.AppendLine();

        Heading(sb, Loc.T("report.txt.declaration"));
        sb.AppendLine(Loc.F("report.declaration", data.ModelName));
        sb.AppendLine("PR-AI Assistant © 2026 Patagonia Robot.");
        sb.AppendLine("══════════════════════════════════════════════════════════");
        
        return sb.ToString();
    }

    /// <summary>Un título del reporte en texto, subrayado a su largo.</summary>
    private static void Heading(StringBuilder sb, string title)
    {
        sb.AppendLine(title);
        sb.AppendLine(new string('─', title.Length));
    }

    /// <summary>
    /// Una fila «Etiqueta:   valor» con los valores alineados en columna. El ancho va
    /// por parámetro y no fijo en el texto: en inglés las etiquetas tienen otro largo.
    /// </summary>
    private static void Row(StringBuilder sb, string label, string? value, int width)
        => sb.AppendLine((label + ":").PadRight(width) + value);

    private static string H(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");

    private string GenerateHtmlContent(SessionExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine($"<html lang=\"{Loc.Language}\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{H(Loc.T("report.html.title"))} - PR-AI Assistant</title>");
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
        
        sb.AppendLine($"<h1>PR-AI Assistant - {H(Loc.T("report.html.title"))}</h1>");

        // Los datos los escribió el perito: se escapan, o un «<» en la descripción rompía
        // el documento.
        sb.AppendLine($"<h2>{H(Loc.T("report.txt.session"))}</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine($"<tr><th>{H(Loc.T("report.perito"))}</th><td>{H(data.Session.PeritoName)} ({H(Loc.T("report.badge"))}: {H(data.Session.PeritoBadge)})</td></tr>");
        sb.AppendLine($"<tr><th>{H(Loc.T("report.case"))}</th><td>{H(data.Session.CaseNumber)} - {H(data.Session.Court)}</td></tr>");
        sb.AppendLine($"<tr><th>{H(Loc.T("report.description"))}</th><td>{H(data.Session.Description)}</td></tr>");
        sb.AppendLine($"<tr><th>{H(Loc.T("report.start"))}</th><td>{data.Session.StartTimeUtc:O}</td></tr>");
        sb.AppendLine($"<tr><th>{H(Loc.T("report.end"))}</th><td>{data.ExportTimeUtc:O}</td></tr>");
        sb.AppendLine($"<tr><th>{H(Loc.T("report.sessionId"))}</th><td>{H(data.Session.SessionId)}</td></tr>");
        sb.AppendLine("</table>");

        sb.AppendLine($"<h2>{H(Loc.F("report.txt.artifacts", data.Artifacts.Count))}</h2>");
        if (data.Artifacts.Count > 0)
        {
            sb.AppendLine("<table>");
            sb.AppendLine($"<tr><th>{H(Loc.T("report.html.name"))}</th><th>{H(Loc.T("report.html.type"))}</th><th>{H(Loc.T("report.html.size"))}</th><th>{H(Loc.T("report.html.notes"))}</th></tr>");
            foreach (var art in data.Artifacts)
            {
                sb.AppendLine($"<tr><td>{H(art.FileName)}</td><td>{H(art.FileType)}</td><td>{art.FileSize} bytes</td><td>{H(art.Notes)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        else
        {
            sb.AppendLine($"<p>{H(Loc.T("report.html.noArtifacts"))}</p>");
        }

        sb.AppendLine($"<h2>{H(Loc.F("report.txt.interactions", data.TotalQueries, data.TotalResponses))}</h2>");
        sb.AppendLine("<div class=\"chat-container\">");
        foreach (var msg in data.Messages)
        {
            string bubbleClass = msg.Role.ToLowerInvariant() switch
            {
                "user" => "bubble-user",
                "assistant" => "bubble-assistant",
                _ => "bubble-system"
            };
            string roleLabel = msg.Role.ToUpperInvariant() switch
            {
                "USER" => Loc.T("report.role.user"),
                "ASSISTANT" => "PR-AI",
                _ => Loc.T("report.role.system")
            };

            sb.AppendLine($"<div class=\"bubble {bubbleClass}\">");
            sb.AppendLine($"<div class=\"timestamp\">{msg.Timestamp:O} | {H(roleLabel)}</div>");
            if (msg.HasImages)
                sb.AppendLine($"<div class=\"timestamp\">{H(Loc.T("report.hasImages"))}</div>");
            sb.AppendLine($"<div>{System.Net.WebUtility.HtmlEncode(msg.Content).Replace("\n", "<br>")}</div>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");
        
        sb.AppendLine("<div class=\"footer-section\">");
        sb.AppendLine($"<h2>{H(Loc.T("report.html.statsAndDeclaration"))}</h2>");
        sb.AppendLine($"<p>{H(Loc.T("report.totalQueries"))}: {data.TotalQueries} | {H(Loc.T("report.totalResponses"))}: {data.TotalResponses} | {H(Loc.T("report.tokens"))}: {data.TotalTokensEstimated} | {H(Loc.T("report.inferenceTime"))}: {data.TotalInferenceMs} ms</p>");
        sb.AppendLine($"<p>{H(Loc.F("report.declaration", data.ModelName)).Replace("\n", " ")}</p>");
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
        return htmlWithoutHash.Replace("<!-- HASH_PLACEHOLDER -->", $"<p><strong>{H(Loc.T("report.html.hash"))}</strong> {hash}</p>");
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
