namespace PRImager.AIAssistant.Localization;

/// <summary>
/// Las capacidades de un modelo, como las ve el usuario: «Visión · Audio». Las claves
/// vienen del catálogo y de la cabecera del .gguf en inglés («Vision», «Audio»).
/// </summary>
public static class CapabilityText
{
    public static string Describe(IEnumerable<string>? capabilities)
    {
        if (capabilities is null) return "";
        return string.Join(" · ", capabilities.Select(c => c switch
        {
            "Vision" => Loc.T("caps.vision"),
            "Audio" => Loc.T("caps.audio"),
            _ => c
        }));
    }
}
