using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PRImager.AIAssistant.Helpers;

/// <summary>
/// Provee la implementación base de INotifyPropertyChanged para los ViewModels (Patrón MVVM).
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Asigna el valor al campo si es distinto y notifica el cambio automático.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
