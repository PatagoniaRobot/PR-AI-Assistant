using System;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// Prepara el entorno WPF que necesitan las pruebas de humo: una Application, los
/// diccionarios de estilo de la aplicación real y las fuentes.
///
/// Existe por una trampa concreta. <c>ShutdownMode</c> vale <c>OnLastWindowClose</c> de
/// fábrica, así que **cuando la primera prueba de humo cierra su ventana, WPF empieza a
/// apagar la aplicación**. La segunda muestra su ventana sin lanzar ninguna excepción
/// pero ya no arma el árbol visual: todas las búsquedas devuelven null y las aserciones
/// fallan hablando de controles que en el XAML están perfectos. Pasó al agregar el humo
/// de E4.4 y costó entender qué miraba mal, porque el síntoma no señala la causa.
///
/// Fijarlo tarde tampoco sirve: una vez que el apagado arrancó, el setter lanza. Tiene
/// que hacerse al crear la Application, y por eso está centralizado acá en lugar de
/// repetido en cada humo, donde el orden de llamada decidiría si funciona.
/// </summary>
static class EntornoWpf
{
    private static bool _preparado;

    public static Application Preparar()
    {
        var app = Application.Current ?? new Application();

        if (!_preparado)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PR-AI-Assistant;component/Styles/ModernControls.xaml")
            });
            app.Resources["MainFont"] = new FontFamily("Segoe UI");
            app.Resources["MonoFont"] = new FontFamily("Consolas");
            _preparado = true;
        }

        return app;
    }
}
