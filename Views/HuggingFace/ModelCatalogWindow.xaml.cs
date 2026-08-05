using System.Windows;
using System.Windows.Input;
using PRImager.AIAssistant.ViewModels.HuggingFace;

namespace PRImager.AIAssistant.Views.HuggingFace;

/// <summary>
/// Catálogo de modelos de HuggingFace. La lógica vive en ModelCatalogViewModel.
/// </summary>
public partial class ModelCatalogWindow : Window
{
    public ModelCatalogWindow()
    {
        InitializeComponent();
    }

    /// <summary>Enter en el buscador dispara la búsqueda.</summary>
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (DataContext is ModelCatalogViewModel vm && vm.SearchCommand.CanExecute(null))
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
