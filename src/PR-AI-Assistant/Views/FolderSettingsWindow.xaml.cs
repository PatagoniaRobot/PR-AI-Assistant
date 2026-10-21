using System.Windows;
using PRImager.AIAssistant.ViewModels;

namespace PRImager.AIAssistant.Views;

public partial class FolderSettingsWindow : Window
{
    public FolderSettingsWindow(FolderSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Cerrar no cancela un movimiento en curso: la ventana es modal, así que mientras se
    /// mueve no se puede llegar a este botón. Si alguna vez deja de serlo, hay que cancelar
    /// y esperar antes de cerrar, como hace la ventana de generación con su motor.
    /// </summary>
    private void Cerrar_Click(object sender, RoutedEventArgs e) => Close();
}
