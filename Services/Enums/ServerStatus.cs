namespace PRImager.AIAssistant.Services.Enums;

/// <summary>
/// Estados posibles del servidor llama-server.
/// </summary>
public enum ServerStatus
{
    Stopped,
    Starting,
    LoadingModel,
    Ready,
    Error
}

/// <summary>
/// Argumentos del evento de cambio de estado del servidor.
/// </summary>
public class ServerStatusChangedEventArgs : EventArgs
{
    public ServerStatus OldStatus { get; }
    public ServerStatus NewStatus { get; }
    public string? ErrorMessage { get; }

    public ServerStatusChangedEventArgs(ServerStatus oldStatus, ServerStatus newStatus, string? errorMessage = null)
    {
        OldStatus = oldStatus;
        NewStatus = newStatus;
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Argumentos del evento de recepción de token en streaming.
/// </summary>
public class TokenReceivedEventArgs : EventArgs
{
    public string Token { get; }

    public TokenReceivedEventArgs(string token)
    {
        Token = token;
    }
}
