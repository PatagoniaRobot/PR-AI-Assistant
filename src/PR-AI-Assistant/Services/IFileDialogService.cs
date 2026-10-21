using System;

namespace PRImager.AIAssistant.Services;

public interface IFileDialogService
{
    string[]? OpenFiles(string title, string filter);
    string[]? OpenImages(string title);
    string[]? OpenTextFiles(string title);
    string? SaveFile(string title, string filter, string defaultFileName);

    /// <summary>
    /// Elige una carpeta. Devuelve null si el usuario cancela.
    /// <paramref name="initialFolder"/> es dónde abrir el diálogo, típicamente la carpeta
    /// que se está por cambiar.
    /// </summary>
    string? SelectFolder(string title, string initialFolder);
}
