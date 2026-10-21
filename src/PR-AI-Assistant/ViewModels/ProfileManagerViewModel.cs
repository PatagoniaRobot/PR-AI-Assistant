using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// ViewModel de la ventana de gestión de perfiles de uso.
/// Los perfiles de fábrica solo se pueden clonar: la UI los bloquea y el servicio
/// rechaza cualquier edición o borrado que se le cuele.
/// </summary>
public class ProfileManagerViewModel : ObservableObject
{
    private readonly UsageProfileService _service;

    public ObservableCollection<UsageProfile> Profiles { get; } = new();

    private UsageProfile? _selectedProfile;

    /// <summary>Perfil seleccionado en la lista. Sus campos se editan en el panel derecho.</summary>
    public UsageProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                LoadEditorFrom(value);
                OnPropertyChanged(nameof(IsEditable));
                OnPropertyChanged(nameof(IsFactorySelected));
                OnPropertyChanged(nameof(HasSelection));
                ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DeleteCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CloneCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True si hay un perfil seleccionado.</summary>
    public bool HasSelection => _selectedProfile != null;

    /// <summary>True si el perfil seleccionado se puede editar (no es de fábrica).</summary>
    public bool IsEditable => _selectedProfile != null && !_selectedProfile.IsFactory;

    /// <summary>True si el perfil seleccionado es de fábrica (para mostrar el aviso).</summary>
    public bool IsFactorySelected => _selectedProfile?.IsFactory == true;

    // === Campos del editor (buffer: no se tocan los perfiles hasta Guardar) ===

    private string _editName = "";
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }

    private string _editSystemPrompt = "";
    public string EditSystemPrompt { get => _editSystemPrompt; set => SetProperty(ref _editSystemPrompt, value); }

    private double _editTemperature = 0.7;
    public double EditTemperature { get => _editTemperature; set => SetProperty(ref _editTemperature, value); }

    private int _editContextTokens = 8192;
    public int EditContextTokens { get => _editContextTokens; set => SetProperty(ref _editContextTokens, value); }

    private bool _editWebSearch;
    public bool EditWebSearchEnabled { get => _editWebSearch; set => SetProperty(ref _editWebSearch, value); }

    private bool _editModelDownload;
    public bool EditModelDownloadEnabled { get => _editModelDownload; set => SetProperty(ref _editModelDownload, value); }

    private bool _editImageGen;
    public bool EditImageGenEnabled { get => _editImageGen; set => SetProperty(ref _editImageGen, value); }

    private bool _editFileUpload;
    public bool EditFileUploadEnabled { get => _editFileUpload; set => SetProperty(ref _editFileUpload, value); }

    private bool _editMandatoryLog;
    public bool EditMandatoryLog { get => _editMandatoryLog; set => SetProperty(ref _editMandatoryLog, value); }

    private string _editAccentColor = "#64748B";
    public string EditAccentColor { get => _editAccentColor; set => SetProperty(ref _editAccentColor, value); }

    private string _statusMessage = "";

    /// <summary>Mensaje de resultado de la última acción.</summary>
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public ICommand CloneCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CloseCommand { get; }

    /// <summary>Solicita el cierre de la ventana.</summary>
    public event EventHandler? CloseRequested;

    public ProfileManagerViewModel(UsageProfileService service)
    {
        _service = service;

        CloneCommand = new RelayCommand(ExecuteClone, () => HasSelection);
        SaveCommand = new RelayCommand(ExecuteSave, () => IsEditable);
        DeleteCommand = new RelayCommand(ExecuteDelete, () => IsEditable);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        Reload(_service.ActiveProfile.Id);
    }

    private void Reload(string? selectId)
    {
        Profiles.Clear();
        foreach (var p in _service.Profiles)
            Profiles.Add(p);

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectId) ?? Profiles.FirstOrDefault();
    }

    private void LoadEditorFrom(UsageProfile? profile)
    {
        if (profile == null) return;
        EditName = profile.Name;
        EditSystemPrompt = profile.SystemPrompt;
        EditTemperature = profile.Temperature;
        EditContextTokens = profile.ContextTokens;
        EditWebSearchEnabled = profile.WebSearchEnabled;
        EditModelDownloadEnabled = profile.ModelDownloadEnabled;
        EditImageGenEnabled = profile.ImageGenEnabled;
        EditFileUploadEnabled = profile.FileUploadEnabled;
        EditMandatoryLog = profile.MandatoryLog;
        EditAccentColor = profile.AccentColor;
    }

    /// <summary>
    /// Clona el perfil seleccionado. Única vía de creación: todo perfil nuevo parte
    /// de una configuración válida.
    /// </summary>
    private void ExecuteClone()
    {
        if (_selectedProfile == null) return;

        var clone = _service.Clone(_selectedProfile, Loc.F("profiles.copyName", _selectedProfile.Name));
        Reload(clone.Id);
        StatusMessage = Loc.F("profiles.created", clone.Name);
    }

    private void ExecuteSave()
    {
        if (_selectedProfile == null || _selectedProfile.IsFactory) return;

        if (string.IsNullOrWhiteSpace(EditName))
        {
            StatusMessage = Loc.T("profiles.emptyName");
            return;
        }

        var edited = new UsageProfile
        {
            Id = _selectedProfile.Id,
            Name = EditName.Trim(),
            SystemPrompt = EditSystemPrompt,
            Temperature = EditTemperature,
            ContextTokens = EditContextTokens,
            DefaultModelPath = _selectedProfile.DefaultModelPath,
            WebSearchEnabled = EditWebSearchEnabled,
            ModelDownloadEnabled = EditModelDownloadEnabled,
            ImageGenEnabled = EditImageGenEnabled,
            FileUploadEnabled = EditFileUploadEnabled,
            MandatoryLog = EditMandatoryLog,
            AccentColor = EditAccentColor
        };

        if (_service.Update(edited))
        {
            Reload(edited.Id);
            StatusMessage = Loc.F("profiles.saved", edited.Name);
        }
        else
        {
            StatusMessage = Loc.T("profiles.saveFailed");
        }
    }

    private void ExecuteDelete()
    {
        if (_selectedProfile == null || _selectedProfile.IsFactory) return;

        var confirm = System.Windows.MessageBox.Show(
            Loc.F("profiles.delete.message", _selectedProfile.Name),
            Loc.T("profiles.delete.title"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        string name = _selectedProfile.Name;
        if (_service.Delete(_selectedProfile.Id))
        {
            Reload(_service.ActiveProfile.Id);
            StatusMessage = Loc.F("profiles.deleted", name);
        }
        else
        {
            StatusMessage = Loc.T("profiles.deleteFailed");
        }
    }
}
