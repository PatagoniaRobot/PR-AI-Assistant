using System.IO;
using System.Windows.Input;
using PRImager.AIAssistant.Controls;
using PRImager.AIAssistant.Helpers;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// Representa un archivo o imagen cargado por el usuario.
///
/// Sirve a los dos destinos posibles, que no son lo mismo: el contexto forense de
/// una investigación —evidencia, auditada, persistente en la sesión— y un adjunto
/// del próximo mensaje en Modo General, que no es evidencia y no sobrevive al turno.
/// </summary>
public class LoadedArtifactViewModel : ObservableObject
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string FileType { get; init; } = string.Empty;
    public long FileSize { get; init; }

    /// <summary>
    /// Texto ya procesado del archivo, cuando el artefacto es un adjunto del mensaje.
    /// Vacío para las imágenes —viajan como imagen, no como texto— y para el contexto
    /// forense, que se acumula en el orquestador y no acá.
    /// </summary>
    public string AttachedContent { get; init; } = string.Empty;

    /// <summary>Distingue el ícono y, sobre todo, cómo viaja al modelo.</summary>
    public bool IsImage { get; init; }

    /// <summary>Ícono para la tira de adjuntos.</summary>
    public IconKind IconKind => IsImage ? IconKind.Image : IconKind.Document;

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
    public static LoadedArtifactViewModel FromFile(
        string filePath, string attachedContent = "", bool isImage = false)
    {
        var info = new FileInfo(filePath);
        return new LoadedArtifactViewModel
        {
            FileName = info.Name,
            FilePath = filePath,
            FileType = info.Extension.ToUpperInvariant(),
            FileSize = info.Exists ? info.Length : 0,
            AttachedContent = attachedContent,
            IsImage = isImage
        };
    }
}
