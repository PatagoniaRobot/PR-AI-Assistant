using System.Windows;
using PRImager.AIAssistant.ViewModels.ImageGen;

namespace PRImager.AIAssistant.Views.ImageGen;

public partial class ImagePackageSelectorWindow : Window
{
    public ImagePackageSelectorWindow(ImagePackageSelectorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // La consulta de VRAM lanza nvidia-smi, así que se hace al cargar y no en el
        // constructor del ViewModel: bloquear el hilo de UI mientras arranca un proceso
        // es lo que dejó la ventana transparente en el defecto 4.10.
        Loaded += async (_, _) =>
        {
            if (DataContext is ImagePackageSelectorViewModel vm)
                await vm.InitializeAsync();
        };
    }
}
