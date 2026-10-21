using System.Diagnostics;
using System.IO;

namespace PRImager.AIAssistant.Helpers;

/// <summary>
/// Abre la ayuda en el navegador, en el idioma de la aplicación.
///
/// Help_PRAIA.html trae los dos idiomas y elige por la dirección: <c>#es</c> o <c>#en</c>.
/// Abrir el archivo con <c>#en</c> al final no sirve: Windows lo toma como parte del nombre
/// o lo descarta al pasarlo al navegador. Por eso se escribe una página puente en la
/// carpeta temporal que redirige con el idioma ya puesto; la redirección la hace el
/// navegador, que sí conserva la marca.
/// </summary>
public static class HelpLauncher
{
    public static string HelpPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Help", "Help_PRAIA.html");

    /// <summary>Abre la ayuda. Devuelve false si el archivo no está.</summary>
    public static bool Open()
    {
        if (!File.Exists(HelpPath)) return false;

        string target = new Uri(HelpPath).AbsoluteUri + "#" + Loc.Language;
        string bridge = Path.Combine(Path.GetTempPath(), "praia-help.html");
        File.WriteAllText(bridge,
            "<!DOCTYPE html><meta charset=\"utf-8\"><script>location.replace(" +
            System.Text.Json.JsonSerializer.Serialize(target) + ");</script>");

        Process.Start(new ProcessStartInfo { FileName = bridge, UseShellExecute = true });
        return true;
    }
}
