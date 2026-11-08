using System.Windows;
using System.Windows.Media;

namespace Nexonic.App;

/// <summary>Loads independent WPF control dictionaries for Dark, Slate and the original PaperGray.</summary>
public static class ThemeManager
{
    public static readonly string[] Names = ["Light", "Dark", "PaperGray", "Slate"];
    static ResourceDictionary? activeTheme;
    public static string CurrentTheme { get; private set; } = "Light";
    public static event Action? ThemeChanged;

    public static void Apply(string? name)
    {
        if (!Names.Contains(name)) name = "Light";
        var colors = name switch
        {
            "Dark" => new[] { "#77ABFF", "#191C22", "#252A32", "#21262E", "#F1F3F6", "#ABB5C1", "#3D4653", "#343B46", "#40506A" },
            "PaperGray" => new[] { "#606064", "#B2B2B3", "#E8E8E8", "#B2B2B3", "#232326", "#464649", "#909093", "#F2DABD", "#F9B04A" },
            "Slate" => new[] { "#8BC5DF", "#202A34", "#2C3945", "#25313D", "#EEF3F7", "#AFC0CE", "#4D5F6B", "#3B4B59", "#4A5C6D" },
            _ => new[] { "#1F6FEB", "#F7F9FC", "#FFFFFF", "#EEF2F7", "#1F2328", "#57606A", "#E3E8EF", "#F3F7FE", "#DCE8FC" }
        };
        var keys = new[] { "AccentBrush", "WindowBrush", "SurfaceBrush", "SidebarBrush",
            "TextBrush", "MutedBrush", "BorderBrushTheme", "HoverBrush", "SelectedBrush" };
        var resources = Application.Current.Resources;
        for (int i = 0; i < keys.Length; i++)
            resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));

        // Load first, then switch, so a parse failure does not leave the previous theme unloaded.
        ResourceDictionary? next = null;
        if (name != "Light")
        {
            var file = name;
            next = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/" + file + ".xaml", UriKind.Absolute)
            };
        }
        if (activeTheme != null) resources.MergedDictionaries.Remove(activeTheme);
        if (next != null) resources.MergedDictionaries.Add(next);
        activeTheme = next;
        // PaperGray's original grey gradient is used behind the toolbar.
        // Other themes retain their own flat surface brush.
        resources["ToolbarBackgroundBrush"] = name == "PaperGray"
            ? next!["NormalBrush"]
            : resources["SurfaceBrush"];
        CurrentTheme = name!;
        resources["ErrorBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(name is "Dark" or "Slate" ? "#FF938F" : "#C73434"));
        ThemeChanged?.Invoke();
    }
}
