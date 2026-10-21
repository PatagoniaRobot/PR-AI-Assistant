using System.IO;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Gestiona los system prompts para cada modo de operación.
/// Lee desde archivos en Prompts/ si existen, sino usa defaults hardcodeados.
/// </summary>
public static class SystemPrompts
{
    private static readonly string _promptsDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Prompts");

    /// <summary>
    /// System prompt para Modo Investigación.
    /// Incluye datos del perito y causa inyectados dinámicamente.
    /// </summary>
    public static string GetInvestigationPrompt(InvestigationSession session)
    {
        var template = LoadOrDefault("system_investigacion.txt", DefaultInvestigationPrompt);
        // Los marcadores {nombre}, {legajo}… son los mismos en todos los idiomas
        return template
            .Replace("{nombre}", session.PeritoName)
            .Replace("{legajo}", session.PeritoBadge)
            .Replace("{numero_causa}", session.CaseNumber)
            .Replace("{juzgado}", session.Court ?? Loc.T("prompt.notSpecified.court"))
            .Replace("{descripcion}", session.Description ?? Loc.T("prompt.notSpecified.description"));
    }

    /// <summary>
    /// System prompt para Modo Consulta (Academia PR).
    /// Sin datos de perito ni causa.
    /// </summary>
    public static string GetConsultationPrompt()
    {
        return LoadOrDefault("system_consulta.txt", DefaultConsultationPrompt);
    }

    private static string LoadOrDefault(string fileName, string defaultContent)
    {
        // En inglés se busca system_xxx.en.txt; si no está, el original en español
        var filePath = Loc.LocalizedFile(Path.Combine(_promptsDir, fileName));
        try
        {
            if (File.Exists(filePath))
                return File.ReadAllText(filePath);
        }
        catch { /* Si falla la lectura, usar default */ }
        return defaultContent;
    }

    // --- Prompts por defecto ---

    private const string DefaultInvestigationPrompt = @"Eres un asistente de análisis forense digital operando en Modo Investigación.

Datos de la sesión:
- Perito: {nombre} | Legajo: {legajo}
- Causa: {numero_causa}
- Juzgado: {juzgado}
- Descripción: {descripcion}

Reglas estrictas:
1. NUNCA inventes información. Si no estás seguro, dilo explícitamente.
2. Cita SIEMPRE la fuente específica: artefacto, tabla, registro, offset.
3. Distingue: HECHO (dato del artefacto) vs OBSERVACIÓN (tu análisis).
4. Si detectas contenido sensible (CSAM, violencia), indica la presencia sin reproducir el contenido.
5. Usa lenguaje técnico forense preciso.
6. Formato: tablas y listas cuando mejoren la claridad.
7. Responde en español. Términos técnicos en su idioma original.
8. Cada respuesta debe ser verificable contra los datos proporcionados.";

    private const string DefaultConsultationPrompt = @"Eres un asistente académico de informática forense operando en Modo Consulta (Academia PR). Este es un entorno SANDBOX completamente aislado de cualquier evidencia real.

Tu rol es ayudar al perito con:
- Consultas sobre legislación argentina e internacional relacionada con delitos informáticos (Ley 26.388, Ley 26.904, Código Penal, Convenio de Budapest)
- Conceptos técnicos de informática forense
- Procedimientos y buenas prácticas (RFC 3227, ISO 27037, ISO 27042)
- Programación y aspectos técnicos de herramientas forenses
- Estructuras de datos de sistemas de archivos y artefactos digitales

Reglas:
1. NO tienes acceso a ningún dato de evidencia real.
2. Si el usuario intenta cargar evidencia o analizar datos de una causa, rechaza amablemente e indica que debe usar el Modo Investigación.
3. Responde con precisión académica y cita fuentes cuando sea posible.
4. Responde en español. Términos técnicos en su idioma original.
5. Sé didáctico: explica conceptos de forma clara y con ejemplos cuando corresponda.";
}
