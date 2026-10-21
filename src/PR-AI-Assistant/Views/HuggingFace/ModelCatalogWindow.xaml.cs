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

    /// <summary>
    /// La fila elegida, a la vista: la recomendada se elige sola al cargar el repositorio
    /// y puede quedar más abajo de lo que muestra la tabla.
    /// </summary>
    private void QuantGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid && grid.SelectedItem != null)
            grid.ScrollIntoView(grid.SelectedItem);
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
