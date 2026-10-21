using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;

namespace PRImager.AIAssistant.Localization;

/// <summary>
/// Los textos de la aplicación, en español o en inglés.
///
/// Decidido por el usuario el 20 de octubre de 2026: arranca en el idioma de Windows —si
/// no es español, en inglés— y se cambia a mano. El idioma se fija al arrancar y no cambia
/// en caliente: los ViewModels arman textos con datos adentro y habría que rehacerlos
/// todos. Cambiarlo pide volver a abrir la aplicación (<see cref="App.Restart"/>).
///
/// Los textos viven en <c>Localization\{idioma}\*.json</c>, incrustados en el ejecutable.
/// Los de búsqueda web, catálogo e imágenes van en archivos propios que la edición
/// Forense no incrusta: la misma barrera de compilación que deja afuera el código.
///
/// Sin <see cref="Initialize"/> —los bancos de prueba cargan el DLL sin pasar por App—
/// queda en español, que es lo que esperan sus aserciones.
/// </summary>
public static class Loc
{
    public const string Spanish = "es";
    public const string English = "en";

    /// <summary>Valor de la configuración que sigue al idioma de Windows.</summary>
    public const string Automatic = "auto";

    private const string ResourcePrefix = "PRAIA.Loc.";

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _active;
    private static Dictionary<string, string>? _fallback;

    /// <summary>El idioma en uso: <see cref="Spanish"/> o <see cref="English"/>.</summary>
    public static string Language { get; private set; } = Spanish;

    public static bool IsEnglish => Language == English;

    /// <summary>Qué idioma corresponde a lo que dice la configuración.</summary>
    public static string Resolve(string? setting) => setting switch
    {
        Spanish => Spanish,
        English => English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Spanish ? Spanish : English
    };

    public static void Initialize(string? setting)
    {
        lock (Gate)
        {
            Language = Resolve(setting);
            _active = Load(Language);
            _fallback = Language == Spanish ? _active : Load(Spanish);
        }
    }

    /// <summary>El texto de una clave. Si falta en inglés se usa el español; si falta en los dos, la clave.</summary>
    public static string T(string key)
    {
        EnsureLoaded();
        if (_active!.TryGetValue(key, out var text)) return text;
        if (_fallback!.TryGetValue(key, out text)) return text;

        Debug.WriteLine($"[Loc] falta la clave «{key}»");
        return key;
    }

    /// <summary>Un texto con datos: <c>{0}</c>, <c>{1}</c>… con el formato numérico de la PC.</summary>
    public static string F(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>
    /// La versión en el idioma activo de un archivo que viaja al lado del ejecutable: en
    /// inglés, <c>Help_PRAIA.html</c> pasa a <c>Help_PRAIA.en.html</c> si existe. El
    /// original es el español; si falta la traducción, se usa el original.
    /// </summary>
    public static string LocalizedFile(string path)
    {
        if (Language == Spanish) return path;
        string candidate = System.IO.Path.ChangeExtension(path, Language + System.IO.Path.GetExtension(path));
        return System.IO.File.Exists(candidate) ? candidate : path;
    }

    /// <summary>
    /// Singular o plural: usa <c>clave.one</c> si <paramref name="count"/> es 1 y
    /// <c>clave.many</c> si no. El número va como <c>{0}</c>; los demás datos, desde <c>{1}</c>.
    /// </summary>
    public static string P(string key, long count, params object?[] args)
    {
        var all = new object?[args.Length + 1];
        all[0] = count;
        Array.Copy(args, 0, all, 1, args.Length);
        return F(key + (count == 1 ? ".one" : ".many"), all);
    }

    /// <summary>Todas las claves de un idioma. Lo usa el banco que compara español con inglés.</summary>
    public static IReadOnlyDictionary<string, string> All(string language) => Load(language);

    private static void EnsureLoaded()
    {
        if (_active is not null) return;
        lock (Gate)
        {
            _active ??= Load(Language);
            _fallback ??= _active;
        }
    }

    private static Dictionary<string, string> Load(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = Assembly.GetExecutingAssembly();
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            string relative = name.Substring(ResourcePrefix.Length).Replace('\\', '/');
            if (!relative.StartsWith(language + "/", StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var doc = JsonDocument.Parse(stream, options);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Repetida: gana la última. El banco de idiomas lo detecta; un Assert acá
                // abriría un diálogo en medio de una corrida desatendida.
                if (result.ContainsKey(prop.Name))
                    Debug.WriteLine($"[Loc] clave repetida «{prop.Name}» en {relative}");
                result[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        return result;
    }
}

/// <summary>
/// Un texto traducido en XAML: <c>Text="{l:T chat.send}"</c>.
/// Devuelve el texto ya resuelto, no un binding: el idioma no cambia en caliente.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
