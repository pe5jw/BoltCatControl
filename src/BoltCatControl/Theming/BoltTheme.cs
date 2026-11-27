using Avalonia;
using Avalonia.Media;

namespace BoltCatControl.Theming;

/// <summary>
/// Kleurthema's, 1:1 overgenomen van BoltSDR (bolt-web/src/themes.ts).
/// De kleuren staan als brushes in Application.Resources, zodat controls
/// live meewisselen als je van thema verandert (zie <see cref="Ui"/>).
/// </summary>
public sealed record BoltTheme(
    string Name,
    string BgDeep, string BgPanel, string BgControl, string BgInput,
    string Accent, string AccentDim,
    string Rx, string Tx, string Green,
    string Text, string TextDim, string Border)
{
    /// <summary>Resource-sleutels, zelfde namen als de BoltSDR CSS variabelen.</summary>
    public static class Keys
    {
        public const string BgDeep = "Bolt.BgDeep", BgPanel = "Bolt.BgPanel",
            BgControl = "Bolt.BgControl", BgInput = "Bolt.BgInput",
            Accent = "Bolt.Accent", AccentDim = "Bolt.AccentDim",
            Rx = "Bolt.Rx", Tx = "Bolt.Tx", Green = "Bolt.Green",
            Text = "Bolt.Text", TextDim = "Bolt.TextDim", Border = "Bolt.Border";
    }

    public void Apply(Application app)
    {
        void Set(string key, string hex) =>
            app.Resources[key] = new SolidColorBrush(Color.Parse(hex));

        Set(Keys.BgDeep, BgDeep);       Set(Keys.BgPanel, BgPanel);
        Set(Keys.BgControl, BgControl); Set(Keys.BgInput, BgInput);
        Set(Keys.Accent, Accent);       Set(Keys.AccentDim, AccentDim);
        Set(Keys.Rx, Rx);               Set(Keys.Tx, Tx);
        Set(Keys.Green, Green);
        Set(Keys.Text, Text);           Set(Keys.TextDim, TextDim);
        Set(Keys.Border, Border);

        // Fluent controls (knoppen, combobox) dezelfde Bolt-look geven
        Set("ButtonBackground", BgControl);
        Set("ButtonBackgroundPointerOver", BgInput);
        Set("ButtonBackgroundPressed", BgInput);
        Set("ButtonForeground", Text);
        Set("ButtonForegroundPointerOver", Text);
        Set("ButtonForegroundPressed", Text);
        Set("ButtonBorderBrush", Border);
        Set("ButtonBorderBrushPointerOver", AccentDim);
        Set("ButtonBorderBrushPressed", Accent);
        Set("ComboBoxBackground", BgControl);
        Set("ComboBoxBackgroundPointerOver", BgInput);
        Set("ComboBoxBorderBrush", Border);
        Set("ComboBoxForeground", Text);
        Set("ComboBoxDropDownBackground", BgPanel);
        Set("TextControlBackground", BgInput);
        Set("TextControlBackgroundPointerOver", BgInput);
        Set("TextControlBackgroundFocused", BgInput);
        Set("TextControlBorderBrush", Border);
        Set("TextControlForeground", Text);
        Set("TextControlForegroundFocused", Text);

        app.Resources["TabItemHeaderFontSize"] = 15d;

        // Fluent accent (vinkjes, focus, selectie) = Bolt accent i.p.v. Windows-blauw
        void SetColor(string key, string hex) => app.Resources[key] = Color.Parse(hex);
        SetColor("SystemAccentColor", Accent);
        SetColor("SystemAccentColorLight1", Accent);
        SetColor("SystemAccentColorLight2", Accent);
        SetColor("SystemAccentColorLight3", Accent);
        SetColor("SystemAccentColorDark1", AccentDim);
        SetColor("SystemAccentColorDark2", AccentDim);
        SetColor("SystemAccentColorDark3", AccentDim);
    }
}

/// <summary>De BoltSDR thema-presets.</summary>
public static class BoltThemes
{
    public static readonly BoltTheme Amber = new("Amber",
        BgDeep: "#0a0c10", BgPanel: "#13171e", BgControl: "#1c2028", BgInput: "#22272f",
        Accent: "#f5c400", AccentDim: "#7a6200",
        Rx: "#00c8ff", Tx: "#ff4444", Green: "#22dd66",
        Text: "#e8ecf4", TextDim: "#8a95a8", Border: "#323848");

    public static readonly BoltTheme Cyan = new("Cyan",
        BgDeep: "#080c10", BgPanel: "#0f1520", BgControl: "#131c28", BgInput: "#182030",
        Accent: "#00e5ff", AccentDim: "#006070",
        Rx: "#00e5ff", Tx: "#ff4444", Green: "#22dd66",
        Text: "#ddf0ff", TextDim: "#6090a8", Border: "#1e3040");

    public static readonly BoltTheme Green = new("Green",
        BgDeep: "#030a03", BgPanel: "#081008", BgControl: "#0c180c", BgInput: "#101e10",
        Accent: "#00ff44", AccentDim: "#006622",
        Rx: "#00ff44", Tx: "#ff4444", Green: "#00ff44",
        Text: "#c0f0c0", TextDim: "#508050", Border: "#1a3020");

    public static readonly IReadOnlyList<BoltTheme> All = [Amber, Cyan, Green];
}
