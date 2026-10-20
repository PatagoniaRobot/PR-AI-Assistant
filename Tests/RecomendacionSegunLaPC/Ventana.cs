#if CATALOGO
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.HuggingFace;
using PRImager.AIAssistant.ViewModels.HuggingFace;
using PRImager.AIAssistant.Views.HuggingFace;

partial class Banco
{
    // --- humo: la ventana del catálogo, con esta PC y un repositorio real ---
    //
    // Por software (RenderTargetBitmap): en la máquina de desarrollo las ventanas WPF
    // salen en blanco en pantalla (maestro 6.4). Va última: una Application por proceso.

    static void CasoVentanaReal()
    {
        Console.WriteLine("\nhumo: la ventana del catálogo, con esta PC");
        using var vm = new ModelCatalogViewModel(
            Path.Combine(Path.GetTempPath(), "BancoRecomendacion", "desc"), 8192,
            Path.Combine(Path.GetTempPath(), "BancoRecomendacion", "logs"),
            hardware: new HardwareInfoService(Tools));
        vm.Repos.Add(new HfRepo { Id = "unsloth/Qwen3.5-9B-GGUF", License = "apache-2.0", Downloads = 0 });
        vm.SelectedRepo = vm.Repos[0];
        Esperar(() => !vm.IsBusy && vm.Quantizations.Count > 0 && vm.GpuText.StartsWith("Tu PC:"), 60);
        vm.SelectedQuantization = vm.Quantizations.FirstOrDefault(q => q.IsRecommended);

        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PR-AI-Assistant;component/Styles/ModernControls.xaml")
        });
        app.Resources["MainFont"] = new FontFamily("Segoe UI");
        app.Resources["MonoFont"] = new FontFamily("Consolas");

        var ventana = new ModelCatalogWindow
        {
            DataContext = vm,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false
        };
        ventana.Show();
        ventana.UpdateLayout();
        ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        Check("la recomendada está seleccionada", vm.SelectedQuantization?.IsRecommended == true);
        string captura = Path.Combine(Path.GetTempPath(), "catalogo-recomendacion.png");
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)ventana.ActualWidth, (int)ventana.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(ventana);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = new FileStream(captura, FileMode.Create)) png.Save(fs);
        Console.WriteLine($"  captura: {captura}");
        ventana.Close();
        app.Shutdown();
    }
}
#endif
