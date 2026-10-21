using System.ComponentModel;
using System.Windows;
using PRImager.AIAssistant.ViewModels.ImageGen;

namespace PRImager.AIAssistant.Views.ImageGen;

/// <summary>
/// Ventana que hospeda la vista de generación de imágenes.
///
/// **Se abre NO MODAL a propósito.** E4.0 midió que los dos motores conviven en esta GPU
/// —`sd-server` ocioso ocupa 0 MiB y el chat responde en 0,3 s mientras se genera una
/// imagen—, y una ventana modal tiraría eso a la basura: el usuario podría hacer una cosa
/// o la otra, nunca las dos. Con esta ventana abierta se sigue conversando con el modelo
/// de lenguaje mientras la imagen se cocina, que es el resultado que esa medición
/// habilitaba.
///
/// Es también la razón por la que E4.5 no navega. La especificación pedía «una vista
/// hermana del chat, no un panel dentro de él»; una ventana cumple esa intención y además
/// no obliga a desmontar el chat —con su conversación, su streaming y su servidor— para
/// mostrar otra pantalla. Ver el desvío anotado en la sección 5 del maestro.
///
/// El cierre es asíncrono: hay que cancelar lo que esté generando y bajar el motor antes
/// de irse, y eso no se puede hacer en un manejador sincrónico sin bloquear el hilo de UI.
/// </summary>
public partial class ImageGenerationWindow : Window
{
    private readonly ImageGenerationViewModel _viewModel;
    private bool _cierreConfirmado;

    public ImageGenerationWindow(ImageGenerationViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_cierreConfirmado) return;

        // Se frena el cierre, se apaga el motor, y recién entonces se cierra de verdad.
        //
        // Sin esto la ventana desaparecería mientras `CancelAndRestartAsync` todavía está
        // relanzando sd-server, y quedaría un proceso vivo sin nadie que lo administre.
        // Es la misma carrera que ShutdownAsync resuelve internamente, un nivel más
        // arriba: acá lo que hay que evitar es que el cierre le gane.
        e.Cancel = true;
        IsEnabled = false;
        Title = Localization.Loc.T("imagegen.window.closing");

        await _viewModel.ShutdownAsync();

        _cierreConfirmado = true;
        _viewModel.Dispose();

        // Close() va a la cola, no acá. Con el motor ya apagado, ShutdownAsync termina sin
        // esperar nada y este método sigue adentro del Closing que lo llamó: un Close()
        // directo lanzaba «No se puede … Close … mientras un elemento Window se está
        // cerrando». Lo vio el usuario el 21 de octubre de 2026 al cerrar la ventana.
        await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
    }
}
