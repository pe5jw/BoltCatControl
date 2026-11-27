using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BoltCat.Core;
using BoltCatControl.Theming;

namespace BoltCatControl;

/// <summary>
/// Instellingen: welke radio (Hamlib model, zoeken op naam), COM poort, baudrate,
/// en geavanceerd (Hamlib cache, VFO-modus, automatisch verbinden).
/// Sluit met true als er opgeslagen is.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly string[] BaudRates = ["Hamlib standaard", "4800", "9600", "19200", "38400", "57600", "115200"];
    private static readonly (string Label, int? Ms)[] CacheChoices =
    [
        ("100 ms — snel (aanbevolen)", 100),
        ("200 ms", 200),
        ("500 ms", 500),
        ("1000 ms — Hamlib standaard", 1000),
        ("niet wijzigen", null),
    ];

    private readonly AppSettings _settings;
    private IReadOnlyList<RigModel> _models = [];
    private RigModel? _selected;

    private readonly TextBox _search;
    private readonly ListBox _results;
    private readonly TextBlock _selectedText;
    private readonly TextBlock _modelsStatus;
    private readonly ComboBox _port;
    private readonly ComboBox _baud;
    private readonly ComboBox _cache;
    private readonly CheckBox _adaptive;
    private readonly CheckBox _vfoMode;
    private readonly CheckBox _autoConnect;
    private readonly TextBlock _error;
    private readonly TaskCompletionSource _modelsLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Klaar met het laden van de radiolijst (voor de screenshot-test).</summary>
    public Task ModelsLoaded => _modelsLoaded.Task;

    /// <summary>Zoekterm invullen (voor de screenshot-test).</summary>
    public void SearchForTest(string text) => _search.Text = text;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "BoltCatControl — instellingen";
        Width = 560;
        Height = 760;
        MinWidth = 480;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Themed(BackgroundProperty, BoltTheme.Keys.BgDeep);

        // ── Radio ─────────────────────────────────────────────────────────
        _search = new TextBox { Watermark = "Zoek je radio: ft-897d, ft-950, ic-7300, 1043 …" };
        _results = new ListBox { Height = 170, FontFamily = Ui.Mono, FontSize = 12, BorderThickness = new Thickness(1), CornerRadius = Ui.Radius }
            .Themed(ListBox.BackgroundProperty, BoltTheme.Keys.BgInput)
            .Themed(ListBox.BorderBrushProperty, BoltTheme.Keys.Border);
        _selectedText = Ui.MonoText(settings.RigModel > 0 ? $"{settings.RigModel}  {settings.RigName}" : "nog geen radio gekozen", 13, BoltTheme.Keys.Accent);
        _modelsStatus = new TextBlock { FontSize = 11 }.Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.TextDim);

        _search.TextChanged += (_, _) => Filter();
        _results.SelectionChanged += (_, _) =>
        {
            if (_results.SelectedItem is RigModel m)
            {
                _selected = m;
                _selectedText.Text = $"{m.Number}  {m.Manufacturer} {m.Model}";
                // FT-817/857/897 kunnen VFO B niet uitlezen zonder te wisselen
                if (IsFt8x7(m)) _vfoMode!.IsChecked = false;
            }
        };

        var radio = Ui.Panel(new StackPanel
        {
            Spacing = 6,
            Children = { Ui.SectionLabel("Radio"), _search, _results, _modelsStatus, Labeled("Gekozen", _selectedText) },
        });

        // ── Verbinding ────────────────────────────────────────────────────
        _port = new ComboBox { MinWidth = 180 };
        var refresh = Ui.Button("Vernieuwen", () => { FillPorts(); return Task.CompletedTask; }, small: true);
        _baud = new ComboBox { MinWidth = 180, ItemsSource = BaudRates };
        _baud.SelectedItem = settings.Baud is { } b ? b.ToString(CultureInfo.InvariantCulture) : BaudRates[0];
        if (_baud.SelectedItem is null) _baud.SelectedIndex = 0;
        FillPorts();

        var connection = Ui.Panel(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.SectionLabel("Verbinding"),
                Labeled("COM poort", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _port, refresh } }),
                Labeled("Baudrate", _baud),
                Hint("Baudrate = de CAT RATE in het menu van de radio."),
            },
        });

        // ── Geavanceerd ───────────────────────────────────────────────────
        _cache = new ComboBox { MinWidth = 220, ItemsSource = CacheChoices.Select(c => c.Label).ToList() };
        _cache.SelectedIndex = Math.Max(0, Array.FindIndex(CacheChoices, c => c.Ms == settings.CacheMs));
        _adaptive = new CheckBox { Content = "Cache vanzelf verhogen als de radio het niet bijhoudt", IsChecked = settings.AdaptiveCache };
        _vfoMode = new CheckBox { Content = "VFO-modus (VFO B uitlezen) — niet voor FT-817/857/897", IsChecked = settings.VfoMode };
        _autoConnect = new CheckBox { Content = "Automatisch verbinden bij starten", IsChecked = settings.AutoConnect };
        foreach (var cb in new[] { _adaptive, _vfoMode, _autoConnect })
            cb.Themed(CheckBox.ForegroundProperty, BoltTheme.Keys.Text);

        var advanced = Ui.Panel(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.SectionLabel("Geavanceerd"),
                Labeled("Hamlib cache", _cache),
                Hint("Lager = de frequentie volgt de VFO knop vlotter. Te laag voor de radio geeft timeouts."),
                _adaptive, _vfoMode, _autoConnect,
            },
        });

        // ── Knoppen ───────────────────────────────────────────────────────
        _error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.Tx);
        var cancel = Ui.Button("Annuleren", () => { Close(false); return Task.CompletedTask; }, minWidth: 100);
        var save = Ui.Button("Opslaan en verbinden", () => { Save(); return Task.CompletedTask; }, minWidth: 170);
        Ui.SetActive(save, true);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Spacing = 10,
                Children = { radio, connection, advanced, _error, Ui.Row([], [cancel, save]) },
            },
        };

        Opened += async (_, _) => await LoadModelsAsync();
    }

    private static bool IsFt8x7(RigModel m) =>
        m.Manufacturer.Equals("Yaesu", StringComparison.OrdinalIgnoreCase) &&
        (m.Model.StartsWith("FT-817") || m.Model.StartsWith("FT-818") || m.Model.StartsWith("FT-857") || m.Model.StartsWith("FT-897"));

    private static Control Labeled(string label, Control control)
    {
        var l = new TextBlock { Text = label, Width = 110, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
            .Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.TextDim);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { l, control } };
    }

    private static TextBlock Hint(string text) =>
        new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(118, -4, 0, 0) }
            .Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.TextDim);

    private void FillPorts()
    {
        var ports = new List<string>();
        try { ports.AddRange(System.IO.Ports.SerialPort.GetPortNames().Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase)); }
        catch (Exception) { /* geen seriële poorten op dit systeem */ }
        if (!string.IsNullOrWhiteSpace(_settings.SerialPort) && !ports.Contains(_settings.SerialPort))
            ports.Insert(0, _settings.SerialPort);
        _port.ItemsSource = ports;
        _port.SelectedItem = ports.Contains(_settings.SerialPort) ? _settings.SerialPort : ports.FirstOrDefault();
    }

    private async Task LoadModelsAsync()
    {
        try { await LoadModelsCoreAsync(); }
        finally { _modelsLoaded.TrySetResult(); }
    }

    private async Task LoadModelsCoreAsync()
    {
        var exe = RigctldProcess.FindExecutable(_settings.RigctldPath);
        if (exe is null)
        {
            _modelsStatus.Text = OperatingSystem.IsWindows()
                ? "rigctld niet gevonden (map 'hamlib' naast BoltCatControl ontbreekt)"
                : "rigctld niet gevonden — installeer Hamlib: sudo apt install libhamlib-utils";
            return;
        }
        _modelsStatus.Text = "Radio's laden…";
        try
        {
            _models = await RigctldProcess.ListModelsAsync(exe);
            _modelsStatus.Text = $"{_models.Count} radio's (Hamlib). Tip: 'Dummy' (1) om zonder radio te testen.";
            _selected = _models.FirstOrDefault(m => m.Number == _settings.RigModel);
            _search.Text = _selected is { } s ? s.Model : "";
            Filter();
        }
        catch (Exception ex)
        {
            _modelsStatus.Text = $"Kon de radiolijst niet laden: {ex.Message}";
        }
    }

    private void Filter()
    {
        var q = _search.Text ?? "";
        var list = q.Trim().Length == 0 ? _models : RigctldProcess.FindModels(_models, q);
        _results.ItemsSource = list.Take(300).ToList();
        if (_selected is { } s && list.Contains(s)) _results.SelectedItem = s;
    }

    private void Save()
    {
        var model = _selected;
        int number = model?.Number ?? _settings.RigModel; // niets gekozen: oude keuze behouden
        var port = _port.SelectedItem as string ?? "";

        if (number <= 0) { _error.Text = "Kies eerst je radio in de lijst."; return; }
        if (number != 1 && string.IsNullOrWhiteSpace(port)) { _error.Text = "Kies de COM poort waar de CAT kabel aan zit."; return; }

        _settings.RigModel = number;
        if (model is not null) _settings.RigName = $"{model.Manufacturer} {model.Model}";
        _settings.SerialPort = number == 1 ? "" : port;
        _settings.Baud = int.TryParse(_baud.SelectedItem as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) ? baud : null;
        _settings.CacheMs = CacheChoices[Math.Max(0, _cache.SelectedIndex)].Ms;
        _settings.AdaptiveCache = _adaptive.IsChecked == true;
        _settings.VfoMode = _vfoMode.IsChecked == true;
        _settings.AutoConnect = _autoConnect.IsChecked == true;
        Close(true);
    }
}
