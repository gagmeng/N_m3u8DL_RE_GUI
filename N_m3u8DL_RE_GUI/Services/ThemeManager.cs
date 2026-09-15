using System;
using System.Linq;
using System.Windows;
using Application = System.Windows.Application;

namespace N_m3u8DL_RE_GUI.Services;

/// <summary>
/// Swaps the app-wide theme dictionary (Themes/Theme.Dark.xaml or Theme.Light.xaml)
/// at runtime. Every brush reference in the XAML is a DynamicResource, so replacing
/// the merged dictionary re-colours the whole visual tree — including already
/// rendered windows and log rows that used SetResourceReference.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    private const string ThemeMarker = "Themes/Theme.";

    /// <summary>Applies the named theme; unknown names fall back to Dark.</summary>
    public static void Apply(string? theme)
    {
        var app = Application.Current;
        if (app == null)
            return;

        var name = theme == Light ? Light : Dark;
        var dictionaries = app.Resources.MergedDictionaries;

        // Idempotent: re-selecting the active theme must not rebuild the dictionary.
        if (dictionaries.Count == 1
            && dictionaries[0].Source?.OriginalString?.Contains(ThemeMarker + name) == true)
        {
            return;
        }

        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Source?.OriginalString?.Contains(ThemeMarker) == true)
                dictionaries.RemoveAt(i);
        }

        dictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"{ThemeMarker}{name}.xaml", UriKind.Relative)
        });
    }
}
