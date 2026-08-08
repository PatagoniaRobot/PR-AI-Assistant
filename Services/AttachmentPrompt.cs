using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Arma el bloque de texto con el que los archivos adjuntos llegan al modelo en
/// Modo General.
///
/// Vive fuera del ViewModel por dos motivos. Es lógica pura y conviene poder
/// ejercerla sin levantar la UI ni un servidor de inferencia. Y sobre todo, es el
/// punto donde se decide qué ve el modelo: un adjunto de Modo General **no** es
/// evidencia y no debe entrar al system prompt como el contexto forense, sino
/// delante del mensaje de este turno y nada más.
/// </summary>
public static class AttachmentPrompt
{
    public const string OpeningMarker = "--- ARCHIVOS ADJUNTOS AL MENSAJE ---";
    public const string ClosingMarker = "--- FIN DE LOS ARCHIVOS ADJUNTOS ---";

    /// <summary>
    /// Devuelve el bloque a anteponer al mensaje, o cadena vacía si no hay nada que
    /// adjuntar. Las imágenes no entran: viajan como imagen por su propio camino.
    /// </summary>
    public static string Build(IEnumerable<string> contenidos)
    {
        var textos = contenidos?.Where(c => !string.IsNullOrEmpty(c)).ToList()
                     ?? new List<string>();

        if (textos.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine(OpeningMarker);
        sb.AppendLine("El usuario adjuntó los siguientes archivos a esta consulta.");
        sb.AppendLine();

        foreach (string t in textos)
        {
            sb.AppendLine(t);
            sb.AppendLine();
        }

        sb.AppendLine(ClosingMarker);
        sb.AppendLine();
        return sb.ToString();
    }
}
