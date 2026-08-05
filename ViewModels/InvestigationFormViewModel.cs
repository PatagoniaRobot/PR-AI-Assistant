using System;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// ViewModel del formulario obligatorio del Modo Investigación.
/// Gestiona dos secciones secuenciales: formulario de identificación y disclaimer RFC 3227.
/// </summary>
public class InvestigationFormViewModel : ObservableObject
{
    // === Datos del formulario ===
    private string _peritoName = "";
    private string _peritoBadge = "";
    private string _caseNumber = "";
    private string _court = "";
    private string _description = "";
    private bool _disclaimerAccepted;

    // Propiedades con notificación (SetProperty)
    public string PeritoName
    {
        get => _peritoName;
        set { SetProperty(ref _peritoName, value); UpdateCommands(); }
    }

    public string PeritoBadge
    {
        get => _peritoBadge;
        set { SetProperty(ref _peritoBadge, value); UpdateCommands(); }
    }

    public string CaseNumber
    {
        get => _caseNumber;
        set { SetProperty(ref _caseNumber, value); UpdateCommands(); }
    }

    public string Court
    {
        get => _court;
        set => SetProperty(ref _court, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool DisclaimerAccepted
    {
        get => _disclaimerAccepted;
        set { SetProperty(ref _disclaimerAccepted, value); UpdateCommands(); }
    }

    // === Visibilidad de secciones ===
    private bool _showForm = true;
    private bool _showDisclaimer;

    public bool ShowForm
    {
        get => _showForm;
        set => SetProperty(ref _showForm, value);
    }

    public bool ShowDisclaimer
    {
        get => _showDisclaimer;
        set => SetProperty(ref _showDisclaimer, value);
    }

    // === Timestamp para disclaimer ===
    public string CurrentTimestamp => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

    // === Comandos ===
    public RelayCommand ContinueCommand { get; }
    public RelayCommand AcceptDisclaimerCommand { get; }
    public RelayCommand BackToFormCommand { get; }
    public ICommand GoBackCommand { get; }

    // === Eventos ===
    /// <summary>Volver a selección de modo</summary>
    public event EventHandler? NavigateBack;
    /// <summary>Sesión lista — pasar al chat</summary>
    public event EventHandler<InvestigationSession>? SessionCreated;

    // Referencia al logger para registrar aceptación del disclaimer
    private readonly ForensicLogger? _logger;

    public InvestigationFormViewModel(ForensicLogger? logger = null)
    {
        _logger = logger;

        ContinueCommand = new RelayCommand(
            () =>
            {
                ShowForm = false;
                ShowDisclaimer = true;
                OnPropertyChanged(nameof(CurrentTimestamp));
            },
            () => !string.IsNullOrWhiteSpace(PeritoName)
               && !string.IsNullOrWhiteSpace(PeritoBadge)
               && !string.IsNullOrWhiteSpace(CaseNumber)
        );

        AcceptDisclaimerCommand = new RelayCommand(
            CreateSession,
            () => DisclaimerAccepted
        );

        BackToFormCommand = new RelayCommand(
            () => { ShowDisclaimer = false; ShowForm = true; DisclaimerAccepted = false; }
        );

        GoBackCommand = new RelayCommand(
            () => NavigateBack?.Invoke(this, EventArgs.Empty)
        );
    }

    /// <summary>
    /// Construye el record inmutable InvestigationSession y lo emite al contenedor.
    /// </summary>
    private void CreateSession()
    {
        // Calcular hash del disclaimer
        var disclaimerText = GetDisclaimerFullText();
        var disclaimerHash = ComputeSha256(disclaimerText);
        var now = DateTime.UtcNow;

        var session = new InvestigationSession
        {
            PeritoName = PeritoName.Trim(),
            PeritoBadge = PeritoBadge.Trim(),
            CaseNumber = CaseNumber.Trim(),
            Court = string.IsNullOrWhiteSpace(Court) ? null : Court.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            StartTimeUtc = now,
            DisclaimerAccepted = true,
            DisclaimerAcceptedTimeUtc = now,
            DisclaimerHash = disclaimerHash
        };

        // Registrar en log forense
        _logger?.LogSessionStart(session.CaseNumber);
        _logger?.LogCustom("PERITO", $"name=\"{session.PeritoName}\" badge=\"{session.PeritoBadge}\"");
        _logger?.LogCustom("CASE", $"id=\"{session.CaseNumber}\" court=\"{session.Court ?? "N/A"}\"");
        _logger?.LogCustom("DISCLAIMER_ACCEPTED", $"hash={disclaimerHash}");

        SessionCreated?.Invoke(this, session);
    }

    /// <summary>
    /// Texto completo del disclaimer para hashear (auditoría).
    /// </summary>
    private string GetDisclaimerFullText()
    {
        return $@"DECLARACIÓN DE CUMPLIMIENTO – PR-AI ASSISTANT
1. CERO ALTERACIÓN: PR-AI Assistant NO accede, modifica ni toca la evidencia digital original.
2. RFC 3227: El orden de volatilidad y las buenas prácticas se respetan en todo momento.
3. TRAZABILIDAD: Toda interacción queda registrada con timestamp UTC, hash SHA-256, e identificación del perito.
4. VALIDACIÓN HUMANA: Ninguna respuesta constituye evidencia ni conclusión pericial.
Perito: {PeritoName} | Legajo: {PeritoBadge} | Causa: {CaseNumber}
Fecha: {CurrentTimestamp}";
    }

    private static string ComputeSha256(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void UpdateCommands()
    {
        ContinueCommand.RaiseCanExecuteChanged();
        AcceptDisclaimerCommand.RaiseCanExecuteChanged();
    }
}
