using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BoltCat.Core;
using BoltCat.Core.Midi;
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
    private readonly ComboBox _scale;
    private static readonly double[] Scales = [0.5, 0.6, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];
    private readonly TextBlock _error;
    private readonly TaskCompletionSource _modelsLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Klaar met het laden van de radiolijst (voor de screenshot-test).</summary>
    public Task ModelsLoaded => _modelsLoaded.Task;

    /// <summary>Zoekterm invullen (voor de screenshot-test).</summary>
    public void SearchForTest(string text) => _search.Text = text;

    // MIDI: werkkopie, pas bij Opslaan definitief; bij Annuleren terug naar de oude
    private readonly MidiController? _midi;
    private MidiSettings _midiWork;
    private ComboBox _midiDevice = null!;
    private TextBlock _midiVfoText = null!, _midiPttText = null!, _midiLearnText = null!, _midiMonitor = null!;
    private ComboBox _midiEncoder = null!, _midiPttMode = null!;
    private CheckBox _midiInvert = null!;
    private readonly Queue<string> _monitorLines = new();
    private bool _updatingMidiUi;

    private static readonly (string Label, EncoderSettings Enc)[] EncoderChoices =
    [
        ("rond 64 (63 / 65)", new(EncoderMode.Offset, 64)),
        ("rond 65 (64 / 66)", new(EncoderMode.Offset, 65)),
        ("1 / 127 (two's complement)", new(EncoderMode.TwosComplement)),
        ("1 / 65 (signed bit)", new(EncoderMode.SignedBit)),
    ];

    public SettingsWindow(AppSettings settings, MidiController? midi = null)
    {
        _settings = settings;
        _midi = midi;
        _midiWork = settings.Midi.Clone();
        Title = "BoltCatControl — instellingen";
        Width = 560;
        Height = 820;
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

        // ── Weergave ──────────────────────────────────────────────────────
        _scale = new ComboBox { MinWidth = 120, ItemsSource = Scales.Select(x => $"{x:P0}").ToList() };
        // Schaal volgt de venstergrootte en kan dus "krom" zijn (bijv. 87 %): dichtstbijzijnde tonen
        _scale.SelectedIndex = Array.IndexOf(Scales, Scales.OrderBy(x => Math.Abs(x - settings.UiScale)).First());
        _scale.SelectionChanged += (_, _) =>
        {
            if (_scale.SelectedIndex >= 0) settings.UiScale = Scales[_scale.SelectedIndex];
        };
        var view = Ui.Panel(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.SectionLabel("Weergave"),
                Labeled("Schaal", _scale),
                Hint("Of sleep het venster groter/kleiner: alles schaalt mee. Ook Ctrl + / Ctrl − / Ctrl 0 of Ctrl + scrollwiel."),
            },
        });

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
                Children = { radio, connection, BuildMidiPanel(), view, advanced, _error, Ui.Row([], [cancel, save]) },
            },
        };

        // Instellingen in dezelfde schaal als het hoofdvenster
        var s = Math.Clamp(settings.UiScale, MainWindow.MinScale, MainWindow.MaxScale);
        if (Math.Abs(s - 1.0) > 0.001)
        {
            Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(s, s), Child = (Control)Content! };
            Width *= s;
            Height *= s;
        }

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

    // ── MIDI ──────────────────────────────────────────────────────────────

    private Control BuildMidiPanel()
    {
        _midiDevice = new ComboBox { MinWidth = 260 };
        var refresh = Ui.Button("Vernieuwen", () => { FillMidiDevices(); return Task.CompletedTask; }, small: true);
        FillMidiDevices();
        _midiDevice.SelectionChanged += (_, _) =>
        {
            if (_updatingMidiUi) return;
            _midiWork.Device = _midiDevice.SelectedIndex <= 0 ? null : _midiDevice.SelectedItem as string;
            ApplyMidiWork();
        };

        _midiVfoText = Ui.MonoText("", 12, BoltTheme.Keys.Accent);
        _midiVfoText.VerticalAlignment = VerticalAlignment.Center;
        var learnVfo = Ui.Button("Leer VFO-knop", () => { _midi?.LearnVfo(); return Task.CompletedTask; }, small: true);

        _midiEncoder = new ComboBox { MinWidth = 220, ItemsSource = EncoderChoices.Select(c => c.Label).ToList() };
        _midiEncoder.SelectionChanged += (_, _) =>
        {
            if (_updatingMidiUi || _midiEncoder.SelectedIndex < 0) return;
            _midiWork.Encoder = EncoderChoices[_midiEncoder.SelectedIndex].Enc with { Invert = _midiInvert.IsChecked == true };
            ApplyMidiWork();
        };
        _midiInvert = new CheckBox { Content = "Richting omdraaien" }.Themed(CheckBox.ForegroundProperty, BoltTheme.Keys.Text);
        _midiInvert.IsCheckedChanged += (_, _) =>
        {
            if (_updatingMidiUi) return;
            _midiWork.Encoder = _midiWork.Encoder with { Invert = _midiInvert.IsChecked == true };
            ApplyMidiWork();
        };

        _midiPttText = Ui.MonoText("", 12, BoltTheme.Keys.Accent);
        _midiPttText.VerticalAlignment = VerticalAlignment.Center;
        var learnPtt = Ui.Button("Leer PTT-knop", () => { _midi?.LearnPtt(); return Task.CompletedTask; }, small: true);
        _midiPttMode = new ComboBox { MinWidth = 220, ItemsSource = new[] { "Vasthouden = zenden", "Druk aan / druk uit" } };
        _midiPttMode.SelectionChanged += (_, _) =>
        {
            if (_updatingMidiUi) return;
            _midiWork.PttMode = _midiPttMode.SelectedIndex == 1 ? PttMode.Toggle : PttMode.Hold;
            ApplyMidiWork();
        };

        _midiLearnText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap }
            .Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.Rx);

        _midiMonitor = Ui.MonoText("(nog niets ontvangen)", 11, BoltTheme.Keys.TextDim);
        var monitor = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = Ui.Radius,
            Padding = new Thickness(8, 6),
            MinHeight = 96,
            Width = 380,
            Child = _midiMonitor,
        }.Themed(Border.BackgroundProperty, BoltTheme.Keys.BgInput)
         .Themed(Border.BorderBrushProperty, BoltTheme.Keys.Border);

        if (_midi is not null)
        {
            _midi.MessageReceived += OnMidiMessage;
            _midi.LearnProgress += OnLearnProgress;
            _midi.LearnFinished += OnLearnFinished;
            Closed += (_, _) =>
            {
                _midi.MessageReceived -= OnMidiMessage;
                _midi.LearnProgress -= OnLearnProgress;
                _midi.LearnFinished -= OnLearnFinished;
                _midi.CancelLearn();
            };
        }
        UpdateMidiUi();

        return Ui.Panel(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.SectionLabel("MIDI"),
                Labeled("Apparaat", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _midiDevice, refresh } }),
                Labeled("VFO-knop", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { learnVfo, _midiVfoText } }),
                Labeled("Encoder", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _midiEncoder, _midiInvert } }),
                Hint("Elke klik = de stap uit het hoofdvenster (10 Hz / 100 Hz / 1 kHz). Sneller draaien = grotere sprong als je controller dat doorgeeft."),
                Labeled("PTT-knop", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { learnPtt, _midiPttText } }),
                Labeled("PTT", _midiPttMode),
                _midiLearnText,
                Labeled("Monitor", monitor),
                Hint("Toont live wat de controller stuurt, handig om te zien welke waarden een knop geeft."),
            },
        });
    }

    private void FillMidiDevices()
    {
        var names = new List<string> { "(geen)" };
        names.AddRange(MidiDevices.List());
        if (_midiWork.Device is { } d && !names.Contains(d)) names.Add(d);
        _updatingMidiUi = true;
        _midiDevice.ItemsSource = names;
        _midiDevice.SelectedItem = _midiWork.Device is { } dev && names.Contains(dev) ? dev : names[0];
        _updatingMidiUi = false;
    }

    private void ApplyMidiWork()
    {
        // MIDI geldt direct én wordt direct bewaard: geleerde knoppen gaan nooit meer
        // verloren als je het venster met het kruisje sluit
        _settings.Midi = _midiWork.Clone();
        _settings.Save();
        if (_midi is null) return;
        var error = _midi.Apply(_midiWork);
        _midiLearnText.Text = error ?? (_midi.IsOpen ? $"Verbonden met {_midi.OpenDevice}" : "");
        UpdateMidiUi();
    }

    private void UpdateMidiUi()
    {
        _updatingMidiUi = true;
        _midiVfoText.Text = _midiWork.Vfo is { } v ? $"{v}  ·  {_midiWork.Encoder}" : "nog niet geleerd — klik op Leer VFO-knop";
        _midiPttText.Text = _midiWork.Ptt is { } p ? p.ToString() : "nog niet geleerd — klik op Leer PTT-knop";
        var enc = _midiWork.Encoder with { Invert = false };
        _midiEncoder.SelectedIndex = Array.FindIndex(EncoderChoices, c => c.Enc == enc);
        _midiInvert.IsChecked = _midiWork.Encoder.Invert;
        _midiPttMode.SelectedIndex = _midiWork.PttMode == PttMode.Toggle ? 1 : 0;
        _updatingMidiUi = false;
    }

    private void OnMidiMessage(MidiMessage m) => Dispatcher.UIThread.Post(() =>
    {
        _monitorLines.Enqueue($"{DateTime.Now:HH:mm:ss.f}  {m}");
        while (_monitorLines.Count > 6) _monitorLines.Dequeue();
        _midiMonitor.Text = string.Join(Environment.NewLine, _monitorLines);
    });

    private void OnLearnProgress(string text) => Dispatcher.UIThread.Post(() => _midiLearnText.Text = text);

    private void OnLearnFinished(MidiSettings? learned, string message) => Dispatcher.UIThread.Post(() =>
    {
        _midiLearnText.Text = message;
        if (learned is null) return;
        learned.Device = _midiWork.Device;
        _midiWork = learned;
        ApplyMidiWork();
        _midiLearnText.Text = message;
    });

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
        _settings.Midi = _midiWork.Clone();
        Close(true);
    }
}
