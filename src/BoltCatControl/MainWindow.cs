using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BoltCat.Core;
using BoltCatControl.Controls;
using BoltCatControl.Theming;

namespace BoltCatControl;

/// <summary>
/// Hoofdvenster in Bolt look-and-feel:
///   header      LED's (rigctld / CAT / TX), radio, verbinden, instellingen, thema
///   banner      foutmelding (radio reageert niet, rigctld niet gevonden, ...)
///   VFO paneel  VFO A per cijfer afstembaar, mode, S-meter, VFO B, stapgrootte
///   PTT         grote zend/ontvang knop
///   knoppen     mode, A↔B, A→B, split, banden
///   statusbalk  rigctld, radio, laatste melding, versie
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly int[] Steps = [10, 100, 1000];

    private readonly RigController _rig;
    private readonly AppSettings _settings;
    private readonly bool _autoStart;
    private readonly string _version;
    private bool _closingDone;

    private readonly Led _ledRigctld = new("RIGCTLD");
    private readonly Led _ledCat     = new("CAT");
    private readonly Led _ledTx      = new("TX", BoltTheme.Keys.Tx);
    private readonly TextBlock _radioName;
    private readonly Button _connectButton;
    private readonly ComboBox _themePicker;

    private readonly Border _banner;
    private readonly TextBlock _bannerText;

    private readonly FrequencyDisplay _vfoA = new(56);
    private readonly TextBlock _modeText;
    private readonly Border _splitBadge;
    private readonly TextBlock _bandText;
    private readonly SMeterBar _meter = new();
    private readonly TextBlock _meterS;
    private readonly TextBlock _meterDb;
    private readonly TextBlock _vfoB;

    private readonly Dictionary<string, Button> _modeButtons = [];
    private readonly Dictionary<string, Button> _bandButtons = [];
    private readonly Dictionary<int, Button> _stepButtons = [];
    private readonly Button _swapButton;
    private readonly Button _copyButton;
    private readonly Button _splitButton;
    private readonly Button _pttButton;

    private readonly StatusPill _pillRigctld = new("rigctld: niet verbonden");
    private readonly StatusPill _pillRig     = new("radio: —");
    private readonly StatusPill _pillNotice  = new("");
    private readonly StatusPill _pillVersion;

    public MainWindow(RigController rig, AppSettings settings, bool autoStart)
    {
        _rig = rig;
        _settings = settings;
        _autoStart = autoStart;
        _version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "?";

        Title     = $"BoltCatControl {_version}";
        Width     = 920;
        Height    = 545;
        MinWidth  = 780;
        MinHeight = 520;
        this.Themed(BackgroundProperty, BoltTheme.Keys.BgDeep);

        // ── Header ────────────────────────────────────────────────────────
        _radioName = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) }
            .Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.TextDim);
        _connectButton = Ui.Button("Verbinden", ToggleConnectAsync, minWidth: 104);
        var settingsButton = Ui.Button("Instellingen", OpenSettingsAsync, minWidth: 104);
        _themePicker = new ComboBox
        {
            ItemsSource = BoltThemes.All.Select(t => t.Name).ToList(),
            MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _themePicker.SelectedIndex = Math.Max(0, BoltThemes.All.ToList().FindIndex(t => t.Name == _settings.Theme));
        _themePicker.SelectionChanged += (_, _) =>
        {
            if (_themePicker.SelectedIndex < 0 || Application.Current is not { } app) return;
            var theme = BoltThemes.All[_themePicker.SelectedIndex];
            theme.Apply(app);
            _settings.Theme = theme.Name;
            ApplyState(_rig.State);
        };

        var leds = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
            Children = { _ledRigctld, _ledCat, _ledTx },
        };
        var title = new TextBlock
        {
            Text = "BoltCatControl", FontSize = 15, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
        }.Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.Text);

        var header = Ui.Panel(
            Ui.Row([leds, title, _radioName], [_connectButton, settingsButton, _themePicker]),
            new Thickness(14, 10, 14, 16));

        // ── Foutbanner ────────────────────────────────────────────────────
        _bannerText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap }
            .Themed(TextBlock.ForegroundProperty, BoltTheme.Keys.Tx);
        _banner = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = Ui.Radius,
            Padding = new Thickness(12, 8),
            Background = new SolidColorBrush(Color.Parse("#2d0a0a")),
            Child = _bannerText,
            IsVisible = false,
        }.Themed(Border.BorderBrushProperty, BoltTheme.Keys.Tx);

        // ── VFO paneel ────────────────────────────────────────────────────
        _vfoA.TuneBy += delta => _rig.TuneBy(delta);

        _modeText = Ui.MonoText("—", 14, BoltTheme.Keys.Rx);
        _modeText.VerticalAlignment = VerticalAlignment.Center;
        _bandText = Ui.MonoText("", 12, BoltTheme.Keys.TextDim);
        _bandText.VerticalAlignment = VerticalAlignment.Center;
        _splitBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 1),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = Ui.MonoText("SPLIT", 11, BoltTheme.Keys.Accent),
        }.Themed(Border.BorderBrushProperty, BoltTheme.Keys.Accent);

        _meterS  = Ui.MonoText("—", 13, BoltTheme.Keys.Accent);
        _meterDb = Ui.MonoText("", 11, BoltTheme.Keys.TextDim);
        _meterDb.Margin = new Thickness(10, 2, 0, 0);
        _meterDb.VerticalAlignment = VerticalAlignment.Center;
        _vfoB    = Ui.MonoText("—", 16, BoltTheme.Keys.Text);
        _vfoB.VerticalAlignment = VerticalAlignment.Center;

        var stepLabel = Ui.SectionLabel("Stap ↑↓");
        stepLabel.Margin = new Thickness(0, 0, 6, 0);
        stepLabel.VerticalAlignment = VerticalAlignment.Center;
        var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        stepRow.Children.Add(stepLabel);
        foreach (var st in Steps)
        {
            var step = st;
            var b = Ui.Button(step >= 1000 ? $"{step / 1000} kHz" : $"{step} Hz",
                () => { _settings.StepHz = step; ApplyState(_rig.State); return Task.CompletedTask; },
                minWidth: 64, small: true);
            _stepButtons[step] = b;
            stepRow.Children.Add(b);
        }

        var vfoALabel = Ui.SectionLabel("VFO A");
        vfoALabel.Margin = new Thickness(2, 0, 12, 0);
        vfoALabel.VerticalAlignment = VerticalAlignment.Center;
        var vfoBLabel = Ui.SectionLabel("VFO B");
        vfoBLabel.Margin = new Thickness(2, 0, 10, 0);
        vfoBLabel.VerticalAlignment = VerticalAlignment.Center;

        var vfoPanel = Ui.Panel(new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Row([vfoALabel, _modeText, _splitBadge], [_bandText]),
                _vfoA,
                _meter,
                Ui.Row([_meterS, _meterDb], []),
                new Border { Height = 4 },
                Ui.Row([vfoBLabel, _vfoB], [stepRow]),
            },
        }, new Thickness(18, 12, 18, 14));

        // ── PTT ───────────────────────────────────────────────────────────
        _pttButton = new Button
        {
            Content = "PTT",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            FontFamily = Ui.Mono,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = Ui.Radius,
            BorderThickness = new Thickness(2),
        };
        ToolTip.SetTip(_pttButton, "Klik om te zenden, nog eens klikken om te ontvangen");
        _pttButton.Click += async (_, _) => await _rig.SetPttAsync(!_rig.State.Ptt);

        var pttLabel = Ui.SectionLabel("Zenden");
        DockPanel.SetDock(pttLabel, Dock.Top);
        var pttPanel = Ui.Panel(new DockPanel { Children = { pttLabel, _pttButton } }, new Thickness(12));
        pttPanel.Width = 170;
        pttPanel.Margin = new Thickness(10, 0, 0, 0);

        var mainRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(pttPanel, Dock.Right);
        mainRow.Children.Add(pttPanel);
        mainRow.Children.Add(vfoPanel);

        // ── Mode + VFO knoppen ────────────────────────────────────────────
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var m in RigModes.Buttons)
        {
            var mode = m;
            var b = Ui.Button(mode, () => _rig.SetModeAsync(mode), minWidth: 64);
            _modeButtons[mode] = b;
            modeRow.Children.Add(b);
        }

        _swapButton  = Ui.Button("A↔B", _rig.SwapVfoAsync, minWidth: 64);
        _copyButton  = Ui.Button("A→B", _rig.CopyAtoBAsync, minWidth: 64);
        _splitButton = Ui.Button("SPLIT", () => _rig.SetSplitAsync(!_rig.State.Split), minWidth: 72);
        ToolTip.SetTip(_swapButton, "VFO A en B omwisselen (FT-817/857/897: A/B wisselen)");
        ToolTip.SetTip(_copyButton, "VFO A naar B kopiëren");
        ToolTip.SetTip(_splitButton, "Split: ontvangen op A, zenden op B");
        var vfoOps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _swapButton, _copyButton, _splitButton } };

        var controls = Ui.Panel(new StackPanel
        {
            Spacing = 0,
            Children =
            {
                Ui.Row([Ui.SectionLabel("Mode")], [Ui.SectionLabel("VFO")]),
                Ui.Row([modeRow], [vfoOps]),
            },
        });

        // ── Banden ────────────────────────────────────────────────────────
        var bandRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var bnd in Bands.All)
        {
            var band = bnd;
            var b = Ui.Button(Bands.Label(band), () => SelectBandAsync(band), minWidth: 58, small: true);
            b.Margin = new Thickness(0, 0, 6, 6);
            _bandButtons[band.Name] = b;
            bandRow.Children.Add(b);
        }
        var bands = Ui.Panel(new StackPanel { Children = { Ui.SectionLabel("Band"), bandRow } },
                             new Thickness(12, 10, 6, 4));

        // ── Statusbalk ────────────────────────────────────────────────────
        _pillVersion = new StatusPill($"v{_version}");
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        _pillRig.Margin = new Thickness(6, 0);
        _pillNotice.Margin = new Thickness(0, 0, 6, 0);
        _pillNotice.IsVisible = false;
        Grid.SetColumn(_pillRig, 1);
        Grid.SetColumn(_pillNotice, 2);
        Grid.SetColumn(_pillVersion, 3);
        status.Children.Add(_pillRigctld);
        status.Children.Add(_pillRig);
        status.Children.Add(_pillNotice);
        status.Children.Add(_pillVersion);

        // ── Alles onder elkaar ────────────────────────────────────────────
        var root = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
        };
        void Add(Control c, int row, double top)
        {
            c.Margin = new Thickness(c.Margin.Left, top, c.Margin.Right, c.Margin.Bottom);
            Grid.SetRow(c, row);
            root.Children.Add(c);
        }
        Add(header, 0, 0);
        Add(_banner, 1, 10);
        Add(mainRow, 2, 10);
        Add(controls, 3, 10);
        Add(bands, 4, 10);
        Add(status, 5, 10);
        Content = root;

        // ── Koppeling met de radio ────────────────────────────────────────
        _rig.StateChanged += s => Dispatcher.UIThread.Post(() => ApplyState(s));
        _rig.Notice += n => Dispatcher.UIThread.Post(() => ShowNotice(n));

        KeyDown += OnKeyDown;
        Opened += async (_, _) => await OnOpenedAsync();
        Closing += OnClosing;

        ApplyState(_rig.State);
    }

    // ── Opstarten / afsluiten ─────────────────────────────────────────────

    private async Task OnOpenedAsync()
    {
        if (!_autoStart) return;
        if (!_settings.IsConfigured)
            await OpenSettingsAsync();
        else if (_settings.AutoConnect)
            await ConnectAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingDone) return;
        e.Cancel = true;
        RememberBand();
        _settings.Save();
        await _rig.DisposeAsync(); // PTT uit, rigctld stoppen
        _closingDone = true;
        Close();
    }

    // ── Verbinden / instellingen ──────────────────────────────────────────

    private async Task ToggleConnectAsync()
    {
        if (_rig.IsActive) await _rig.DisconnectAsync();
        else if (!_settings.IsConfigured) await OpenSettingsAsync();
        else await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        _connectButton.IsEnabled = false;
        try { await _rig.ConnectAsync(_settings.ToConnection()); }
        finally { _connectButton.IsEnabled = true; ApplyState(_rig.State); }
    }

    private async Task OpenSettingsAsync()
    {
        var dialog = new SettingsWindow(_settings);
        if (await dialog.ShowDialog<bool>(this))
        {
            _settings.Save();
            await ConnectAsync();
        }
    }

    // ── Banden ────────────────────────────────────────────────────────────

    private void RememberBand()
    {
        if (_rig.State.Connected && _rig.DisplayFrequency is { } f && Bands.Find(f) is { } current)
            _settings.BandMemory[current.Name] = f;
    }

    private async Task SelectBandAsync(Band band)
    {
        RememberBand();
        var target = _settings.BandMemory.TryGetValue(band.Name, out var f) && band.Contains(f) ? f : band.Default;
        var mode = RigModes.ToButton(_rig.State.Mode);
        _rig.TuneTo(target);
        // Zijband meenemen: LSB onder 10 MHz, USB erboven (alleen als je in SSB zit)
        var sideband = RigModes.SidebandFor(target);
        if (mode is "LSB" or "USB" && sideband != mode)
            await _rig.SetModeAsync(sideband);
    }

    // ── Toetsenbord ───────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_rig.State.Connected) return;
        switch (e.Key)
        {
            case Key.Up:       _rig.TuneBy(_settings.StepHz);       e.Handled = true; break;
            case Key.Down:     _rig.TuneBy(-_settings.StepHz);      e.Handled = true; break;
            case Key.PageUp:   _rig.TuneBy(_settings.StepHz * 10);  e.Handled = true; break;
            case Key.PageDown: _rig.TuneBy(-_settings.StepHz * 10); e.Handled = true; break;
        }
    }

    // ── Toestand tonen ────────────────────────────────────────────────────

    private void ShowNotice(string text)
    {
        _pillNotice.Text = $"{DateTime.Now:HH:mm:ss}  {text}";
        _pillNotice.IsVisible = true;
    }

    /// <summary>Melding tonen (ook gebruikt door de screenshot-test).</summary>
    public void ShowNoticeForTest(string text) => ShowNotice(text);

    /// <summary>Toestand van de radio op het scherm zetten (ook gebruikt door de screenshot-test).</summary>
    public void ApplyState(RigState s)
    {
        bool connected = s.Connected;
        bool radioOk = connected && s.Error is null;

        // Header
        _ledRigctld.IsOn = _rig.RigctldRunning || connected;
        _ledCat.IsOn = radioOk;
        _ledTx.IsOn = s.Ptt;
        _radioName.Text = _settings.RigModel > 0
            ? $"{_settings.RigName}  ·  {(_settings.RigModel == 1 ? "geen poort" : _settings.SerialPort)}{(_settings.Baud is { } baud ? $"  ·  {baud} baud" : "")}"
            : "geen radio gekozen";
        _connectButton.Content = _rig.IsActive ? "Verbreken" : "Verbinden";

        // Banner
        var error = s.Error is { Length: > 0 } e && e != "Verbinden…" ? e : null;
        _banner.IsVisible = error is not null;
        _bannerText.Text = error is null ? "" : connected ? $"⚠  {error}" : $"✕  {error}";

        // VFO A
        var freq = connected ? _rig.DisplayFrequency ?? s.FrequencyA : null;
        _vfoA.Hz = freq;
        _vfoA.Interactive = connected;
        var modeButton = RigModes.ToButton(s.Mode);
        _modeText.Text = connected
            ? (string.IsNullOrEmpty(s.Mode) ? "?" : s.Mode) + (s.Passband is > 0 ? $"  {s.Passband / 1000.0:0.0#} kHz" : "")
            : "—";
        _splitBadge.IsVisible = connected && s.Split;
        var band = Bands.Find(freq);
        _bandText.Text = !connected ? "" : band is null ? "buiten amateurband" : $"{Bands.Label(band)} band";

        // S-meter (niet tijdens TX: dan geeft de radio een zendmeter door)
        _meter.Tx = s.Ptt;
        _meter.SetDb(connected && !s.Ptt ? s.StrengthDb : null);
        if (!connected)                  { _meterS.Text = "—";  _meterDb.Text = ""; }
        else if (s.Ptt)                  { _meterS.Text = "TX"; _meterDb.Text = ""; }
        else if (s.StrengthDb is { } db) { _meterS.Text = SMeter.Format(db); _meterDb.Text = $"{db} dB"; }
        else                             { _meterS.Text = "S-meter n.v.t."; _meterDb.Text = ""; }

        // VFO B
        _vfoB.Text = connected && s.FrequencyB is { } fb ? FrequencyDisplay.Format(fb)
                   : connected && !_rig.VfoBAvailable ? "niet uitleesbaar (VFO-modus uit)"
                   : "—";
        _vfoB.FontSize = connected && s.FrequencyB is null ? 12 : 16;

        // Knoppen
        foreach (var (m, b) in _modeButtons)  { b.IsEnabled = connected; Ui.SetActive(b, connected && m == modeButton); }
        foreach (var (n, b) in _bandButtons)  { b.IsEnabled = connected; Ui.SetActive(b, connected && band?.Name == n); }
        foreach (var (st, b) in _stepButtons) Ui.SetActive(b, st == _settings.StepHz);
        _swapButton.IsEnabled = _copyButton.IsEnabled = _splitButton.IsEnabled = connected;
        Ui.SetActive(_splitButton, connected && s.Split);

        // PTT
        _pttButton.IsEnabled = connected;
        if (s.Ptt)
        {
            _pttButton.Content = "TX";
            _pttButton.Themed(BackgroundProperty, BoltTheme.Keys.Tx);
            _pttButton.Themed(BorderBrushProperty, BoltTheme.Keys.Tx);
            _pttButton.Foreground = Brushes.White;
        }
        else
        {
            _pttButton.Content = "PTT";
            _pttButton.ClearValue(BackgroundProperty);
            _pttButton.Themed(BorderBrushProperty, BoltTheme.Keys.Border);
            _pttButton.ClearValue(ForegroundProperty);
        }

        // Statusbalk
        _pillRigctld.Text = connected ? "rigctld: verbonden"
                          : _rig.IsConnecting ? "rigctld: starten…"
                          : "rigctld: niet verbonden";
        _pillRigctld.IsOk = connected;
        _pillRig.Text = connected ? (radioOk ? "radio: OK" : "radio: reageert niet") : "radio: —";
        _pillRig.IsOk = radioOk;
    }
}
