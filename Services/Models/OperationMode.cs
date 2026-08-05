namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Modo de operación de PR-AI Assistant.
/// Investigation: análisis forense con cadena de custodia.
/// Consultation: consultas académicas en sandbox aislado.
/// </summary>
public enum OperationMode
{
    /// <summary>Análisis de evidencia con cadena de custodia, formulario obligatorio, acceso al Blackboard</summary>
    Investigation,
    
    /// <summary>Consultas técnicas/legales sin acceso a evidencia (Academia PR)</summary>
    Consultation
}
