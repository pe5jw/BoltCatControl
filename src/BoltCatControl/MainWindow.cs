using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BoltCatControl.Theming;

namespace BoltCatControl;

/// <summary>
/// Hoofdvenster in Bolt look-and-feel. Dit is het visuele skelet (stap 1):
/// header met LED's, VFO A paneel en statusbalk, nog zonder CAT-koppeling.
/// De echte bediening (VFO A/B, mode, PTT, band, S-meter) komt in stap 4.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly Led _ledRigctld = new("RIGCTLD");
    private readonly Led _ledCat     = new("CAT");
    private readonly Led _ledTx      = new("TX", BoltTheme.Keys.Tx);

    private readonly StatusPill _pillRigctld = new("rigctld: niet gestart");
    private readonly StatusPill _pillRig     = new("rig: —");

    public MainWindow()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "?";

        Title     = $"BoltCatControl {version}";
        Width     = 760;
        Height    = 460;
        MinWidth  = 560;
        MinHeight = 380;
        this.Themed(BackgroundProperty, BoltTheme.Keys.BgDeep);

        var root = new DockPanel { Margin = new Thickness(12), LastChildFill = true };

        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var status = BuildStatusBar(version);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        root.Children.Add(BuildVfoPanel());

        Content = root;
    }

    private Control BuildHeader()
    {
        var leds = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _ledRigctld, _ledCat, _ledTx },
        };

        var title = new TextBlock
        {
            Text = "BoltCatControl",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        }.Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.Text);

        var themePicker = new ComboBox
        {
            ItemsSource = BoltThemes.All.Select(t => t.Name).ToList(),
            SelectedIndex = 0,
            MinWidth = 100,
            VerticalAlignment = VerticalAlignment.Center,
        };
        themePicker.SelectionChanged += (_, _) =>
        {
            if (themePicker.SelectedIndex >= 0 && Application.Current is { } app)
                BoltThemes.All[themePicker.SelectedIndex].Apply(app);
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(leds, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(themePicker, 2);
        grid.Children.Add(leds);
        grid.Children.Add(title);
        grid.Children.Add(themePicker);

        var panel = Ui.Panel(grid, new Thickness(14, 10, 14, 16));
        panel.Margin = new Thickness(0, 0, 0, 10);
        return panel;
    }

    private static Control BuildVfoPanel()
    {
        // Frequentie in BoltSDR-formaat (MHz met 6 decimalen), accentkleur
        var freq = Ui.MonoText("14.074000", 54, BoltTheme.Keys.Accent);
        freq.FontWeight = FontWeight.SemiBold;

        // S-meter balk (placeholder waarde)
        var fill = new Border { CornerRadius = new CornerRadius(2) }
            .Themed(Border.BackgroundProperty, BoltTheme.Keys.Green);
        var track = new Grid { Height = 8, ColumnDefinitions = new ColumnDefinitions("45*,55*") }
            .Themed(Panel.BackgroundProperty, BoltTheme.Keys.BgInput);
        Grid.SetColumn(fill, 0);
        track.Children.Add(fill);

        var meterLabels = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 3, 0, 0) };
        var dbm = Ui.MonoText("-73.0 dBm", 11);
        var sval = Ui.MonoText("S9", 11, BoltTheme.Keys.Accent);
        Grid.SetColumn(sval, 1);
        meterLabels.Children.Add(dbm);
        meterLabels.Children.Add(sval);

        var mode = Ui.MonoText("USB", 13, BoltTheme.Keys.Rx);

        var content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.SectionLabel("VFO A"),
                freq,
                mode,
                new Border { Height = 6 },
                track,
                meterLabels,
            },
        };

        return Ui.Panel(content, new Thickness(16, 12));
    }

    private Control BuildStatusBar(string version)
    {
        var versionPill = new StatusPill($"v{version}");
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,Auto"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        _pillRig.Margin = new Thickness(6, 0);
        Grid.SetColumn(_pillRig, 1);
        Grid.SetColumn(versionPill, 2);
        grid.Children.Add(_pillRigctld);
        grid.Children.Add(_pillRig);
        grid.Children.Add(versionPill);
        return grid;
    }
}
