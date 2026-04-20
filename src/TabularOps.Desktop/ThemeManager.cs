using System.IO;
using System.Windows;

namespace TabularOps.Desktop;

/// <summary>
/// Swaps the active color ResourceDictionary at runtime to toggle dark/light theme.
/// All color resources must be bound via {DynamicResource} in XAML for the swap to
/// take effect without restarting the app.
/// </summary>
public static class ThemeManager
{
    private static readonly string PreferenceFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabularOps", "theme");

    private static readonly Uri DarkUri  = new("/Themes/Colors.xaml",      UriKind.Relative);
    private static readonly Uri LightUri = new("/Themes/ColorsLight.xaml",  UriKind.Relative);

    public static bool IsDark { get; private set; } = true;

    /// <summary>Loads the persisted preference (or defaults to dark) and applies it.</summary>
    public static void Initialize()
    {
        bool dark = true;
        try
        {
            if (File.Exists(PreferenceFile))
                dark = File.ReadAllText(PreferenceFile).Trim() != "light";
        }
        catch { }

        Apply(dark);
    }

    public static void Toggle() => Apply(!IsDark);

    public static void Apply(bool dark)
    {
        IsDark = dark;

        var dicts = Application.Current.Resources.MergedDictionaries;

        // Find the currently active colors dict by URI fragment and replace it
        for (int i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Colors"))
            {
                dicts[i] = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
                break;
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferenceFile)!);
            File.WriteAllText(PreferenceFile, dark ? "dark" : "light");
        }
        catch { }
    }
}
