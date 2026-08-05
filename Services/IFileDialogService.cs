using System;

namespace PRImager.AIAssistant.Services;

public interface IFileDialogService
{
    string[]? OpenFiles(string title, string filter);
    string[]? OpenImages(string title);
    string[]? OpenTextFiles(string title);
    string? SaveFile(string title, string filter, string defaultFileName);
}
