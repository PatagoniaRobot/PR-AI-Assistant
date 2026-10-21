using System.Windows;
using System.Windows.Controls;
using PRImager.AIAssistant.ViewModels.ImageGen;

namespace PRImager.AIAssistant.Views.ImageGen;

/// <summary>
/// Vista de generación de imágenes. Es hermana del chat, no un panel adentro de él: se
/// llega desde la barra lateral, que se cablea en E4.5.
/// </summary>
public partial class ImageGenerationView : UserControl
{
    public ImageGenerationView()
    {
        InitializeComponent();

        // La consulta de VRAM lanza nvidia-smi, así que va al cargar y no en el
        // constructor del ViewModel: bloquear el hilo de UI mientras arranca un proceso
        // es lo que dejó la ventana transparente en el defecto 4.10.
        Loaded += async (_, _) =>
        {
            if (DataContext is ImageGenerationViewModel vm)
                await vm.InitializeAsync();
        };

        // Cerrar la vista con una generación en curso la cancela y baja el motor, que es
        // uno de los criterios de E4.4.
        //
        // Unloaded puede dispararse también si WPF reparenta el control, y por eso
        // ShutdownAsync es idempotente: la segunda llamada no hace nada. El descarte del
        // Task es deliberado —Unloaded no puede esperar— y el propio método atrapa sus
        // excepciones, así que no queda ninguna sin observar.
        Unloaded += (_, _) =>
        {
            if (DataContext is ImageGenerationViewModel vm)
                _ = vm.ShutdownAsync();
        };
    }
}
