using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PRImager.AIAssistant.Helpers;

/// <summary>
/// Barra de título oscura de Windows 10/11, para que el marco combine con el fondo.
///
/// Hasta el 21 de octubre de 2026 solo la ponía la ventana principal, y todas las demás
/// —generación de imágenes, modelos de imagen, catálogo, perfiles, carpetas— abrían con
/// el marco blanco. Ahora la registra <see cref="App"/> para toda ventana que se cargue,
/// así una ventana nueva no tiene que acordarse.
/// </summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Se engancha una vez, al arrancar: vale para todas las ventanas de WPF.</summary>
    public static void RegisterForAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) Apply(w); }));

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        }
        catch
        {
            // Silenciar en sistemas que no soporten la API
        }
    }
}
