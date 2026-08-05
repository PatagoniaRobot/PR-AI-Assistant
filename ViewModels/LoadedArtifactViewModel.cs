using System.IO;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// Representa un artefacto (archivo o imagen) cargado en el contexto forense.
/// </summary>
public class LoadedArtifactViewModel : ObservableObject
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string FileType { get; init; } = string.Empty;
    public long FileSize { get; init; }

    /// <summary>Tamaño formateado para mostrar en UI.</summary>
    public string FileSizeFormatted => FileSize < 1024
        ? $"{FileSize} B"
        : FileSize < 1048576
            ? $"{FileSize / 1024.0:F1} KB"
            : $"{FileSize / 1048576.0:F1} MB";

    /// <summary>Comando para eliminar este artefacto de la lista.</summary>
    public ICommand? RemoveCommand { get; set; }

    /// <summary>
    /// Crea un LoadedArtifactViewModel a partir de una ruta de archivo.
    /// </summary>
    public static LoadedArtifactViewModel FromFile(string filePath)
    {
        var info = new FileInfo(filePath);
        return new LoadedArtifactViewModel
        {
            FileName = info.Name,
            FilePath = filePath,
            FileType = info.Extension.ToUpperInvariant(),
            FileSize = info.Exists ? info.Length : 0
        };
    }
}
