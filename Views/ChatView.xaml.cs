using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PRImager.AIAssistant.ViewModels;

namespace PRImager.AIAssistant.Views;

/// <summary>
/// UserControl que contiene la interfaz completa del chat.
/// Extraído de MainWindow para permitir navegación entre vistas.
/// </summary>
public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncApiKeyBox();
    }

    /// <summary>
    /// Refleja la API key guardada en el PasswordBox. PasswordBox no soporta binding
    /// (por diseño: no expone la contraseña como DependencyProperty), así que se
    /// sincroniza a mano en ambos sentidos.
    /// </summary>
    private void SyncApiKeyBox()
    {
        if (BraveApiKeyBox == null) return;
        if (DataContext is MainViewModel vm && BraveApiKeyBox.Password != vm.BraveApiKey)
            BraveApiKeyBox.Password = vm.BraveApiKey;
    }

    /// <summary>
    /// Persiste la API key de Brave al escribirla. Va a la configuración de usuario
    /// en %APPDATA%, nunca al repositorio.
    /// </summary>
    private void BraveApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is System.Windows.Controls.PasswordBox box)
            vm.BraveApiKey = box.Password;
    }

    /// <summary>
    /// Abre una fuente web en el navegador predeterminado, para que el usuario verifique.
    /// </summary>
    private void Source_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Una URL malformada o sin handler no debe tumbar la vista
        }
        e.Handled = true;
    }

    // --- UX Event Handlers (migrados desde MainWindow.xaml.cs) ---

    /// <summary>
    /// Enter envía mensaje, Shift+Enter inserta salto de línea.
    /// </summary>
    private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Shift+Enter → permitir salto de línea (no hacer nada)
                return;
            }

            // Enter solo → enviar mensaje, marcar Handled para evitar salto de línea
            e.Handled = true;
            var vm = DataContext as MainViewModel;
            if (vm?.SendMessageCommand.CanExecute(null) == true)
                vm.SendMessageCommand.Execute(null);
        }
    }

    /// <summary>
    /// Auto-scroll automático cuando crecen los mensajes.
    /// </summary>
    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0 && sender is ScrollViewer sv)
        {
            sv.ScrollToEnd();
        }
    }

    /// <summary>
    /// Mostrar panel derecho de contexto forense.
    /// </summary>
    private void ToggleRightPanel_Click(object sender, RoutedEventArgs e)
    {
        RightPanelColumn.Width = new GridLength(280);
    }

    /// <summary>
    /// Ocultar panel derecho de contexto forense.
    /// </summary>
    private void CollapseRightPanel_Click(object sender, RoutedEventArgs e)
    {
        RightPanelColumn.Width = new GridLength(0);
    }

    /// <summary>
    /// Drag and drop de archivos sobre el chat.
    /// </summary>
    private async void ChatView_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        var vm = DataContext as MainViewModel;
        if (vm == null) return;

        await vm.HandleDroppedFilesAsync(files);
    }

    /// <summary>
    /// Abrir el archivo HTML de ayuda en el navegador predeterminado.
    /// </summary>
    private void OpenHelp_Click(object sender, RoutedEventArgs e)
    {
        string helpPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Help", "Help_PRAIA.html");
        if (System.IO.File.Exists(helpPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = helpPath,
                UseShellExecute = true
            });
        }
    }
}
