using Windows.UI.ViewManagement;
using WinColor = Windows.UI.Color;

namespace AutoBright;

/// <summary>
/// Mirrors the user's Windows accent colour into the app's <c>Accent</c> and
/// <c>AccentText</c> resources and keeps them current: they are updated when
/// the accent changes in Settings, when the app switches light/dark, and when
/// high-contrast mode turns on or off. Call <see cref="Initialize"/> once, from
/// the App constructor. XAML must reference these with
/// <c>DynamicResource</c> so the change shows up live.
/// </summary>
internal static class SystemAccent
{
    // Kept in static fields: the WinRT objects must stay alive for their
    // change events to keep firing.
    private static UISettings? _uiSettings;
    private static AccessibilitySettings? _accessibility;

    public static void Initialize(Application app)
    {
        try
        {
            _uiSettings = new UISettings();
            _accessibility = new AccessibilitySettings();
        }
        catch
        {
            // WinRT settings unavailable: keep the default accent from AppStyles.xaml.
            return;
        }

        Apply(app);

        app.RequestedThemeChanged += (_, _) => Apply(app);
        try
        {
            // Fires on a background thread, so marshal to the UI thread. It
            // also fires when high contrast is toggled (the system colours
            // change), which is why AccessibilitySettings.HighContrastChanged
            // isn't used: subscribing to it throws 0x80070490 in unpackaged
            // desktop apps like this one.
            _uiSettings.ColorValuesChanged += (_, _) => app.Dispatcher.Dispatch(() => Apply(app));
        }
        catch
        {
            // Live updates unavailable; the accent read at startup still applies.
        }
    }

    private static void Apply(Application app)
    {
        if (_uiSettings == null)
        {
            return;
        }

        Color accent;
        Color onAccent;
        try
        {
            if (_accessibility?.HighContrast == true)
            {
                // High contrast: use the system highlight pair the user chose.
#pragma warning disable CS0618 // UIElementColor is the documented way to read HC system colours.
                accent = ToMaui(_uiSettings.UIElementColor(UIElementType.Highlight));
                onAccent = ToMaui(_uiSettings.UIElementColor(UIElementType.HighlightText));
#pragma warning restore CS0618
            }
            else
            {
                // Match the shades WinUI uses for accent fills and accent text:
                // darker in light mode, lighter in dark mode, so it stays legible.
                bool dark = app.RequestedTheme == AppTheme.Dark;
                accent = ToMaui(_uiSettings.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1));
                onAccent = PickTextColor(accent);
            }
        }
        catch
        {
            return;
        }

        app.Resources["Accent"] = accent;
        app.Resources["AccentText"] = onAccent;
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
    internal static Color PickTextColor(Color background)
    {
        static double Linear(float c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        double luminance = 0.2126 * Linear(background.Red)
                           + 0.7152 * Linear(background.Green)
                           + 0.0722 * Linear(background.Blue);
        // Contrast against white vs. black; pick the larger.
        double contrastWhite = 1.05 / (luminance + 0.05);
        double contrastBlack = (luminance + 0.05) / 0.05;
        return contrastWhite >= contrastBlack ? Colors.White : Colors.Black;
    }

    private static Color ToMaui(WinColor c) => Color.FromRgba(c.R, c.G, c.B, c.A);
}
