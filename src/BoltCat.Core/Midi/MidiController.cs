using System.Text.Json.Serialization;

namespace BoltCat.Core.Midi;

[JsonConverter(typeof(JsonStringEnumConverter<PttMode>))]
public enum PttMode
{
    /// <summary>Zenden zolang de knop ingedrukt is.</summary>
    Hold,
    /// <summary>Druk = zenden, nog eens drukken = ontvangen.</summary>
    Toggle,
}

/// <summary>MIDI instellingen (in settings.json).</summary>
public sealed class MidiSettings
{
    public bool Enabled { get; set; } = true;
    public string? Device { get; set; }
    public MidiControl? Vfo { get; set; }
    public EncoderSettings Encoder { get; set; } = new();
    public MidiControl? Ptt { get; set; }
    public PttMode PttMode { get; set; } = PttMode.Hold;

    public MidiSettings Clone() => new()
    {
        Enabled = Enabled, Device = Device, Vfo = Vfo, Encoder = Encoder, Ptt = Ptt, PttMode = PttMode,
    };
}

/// <summary>
/// MIDI → radio:
///   VFO encoder: elke klik = stap (10/100/1000 Hz) × waarde van de encoder (sneller draaien = grotere sprong)
///   PTT knop   : vasthouden of aan/uit
/// Plus leren: VFO (rechtsom, dan linksom draaien) en PTT (knop indrukken).
/// </summary>
public sealed class MidiController : IDisposable
{
    private readonly RigController _rig;
    private readonly Func<int> _stepHz;
    private readonly object _lock = new();
    private IMidiInput? _input;
    private MidiSettings _settings = new();

    // Leren
    private enum LearnPhase { None, VfoRight, VfoLeft, Ptt }
    private LearnPhase _phase;
    private MidiControl? _learnControl;
    private readonly List<int> _right = [], _left = [];
    private long _lastLearnMsg;
    private Timer? _learnTimer;
    private long _learnStarted;

    public MidiController(RigController rig, Func<int> stepHz)
    {
        _rig = rig;
        _stepHz = stepHz;
    }

    public MidiSettings Settings => _settings;
    public string? OpenDevice => _input?.Name;
    public bool IsOpen => _input is not null;
    public bool IsLearning => _phase != LearnPhase.None;

    /// <summary>Elk binnenkomend bericht (voor de monitor en de MIDI LED). Achtergrondthread.</summary>
    public event Action<MidiMessage>? MessageReceived;
    public event Action<string>? Notice;

    /// <summary>Voortgang van het leren ("Draai nu linksom…").</summary>
    public event Action<string>? LearnProgress;

    /// <summary>Leren klaar: geleerde instellingen (of null bij mislukken) + uitleg.</summary>
    public event Action<MidiSettings?, string>? LearnFinished;

    // ── Instellingen / apparaat ───────────────────────────────────────────

    /// <summary>Instellingen toepassen en het apparaat (her)openen. Geeft een foutmelding terug of null.</summary>
    public string? Apply(MidiSettings settings)
    {
        _settings = settings.Clone();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Device))
        {
            Close();
            return null;
        }
        if (_input?.Name == settings.Device) return null;
        try
        {
            Attach(MidiDevices.Open(settings.Device));
            Notice?.Invoke($"MIDI: {settings.Device}");
            return null;
        }
        catch (Exception ex)
        {
            Close();
            Notice?.Invoke($"MIDI: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>Een ingang koppelen (ook voor tests met een nep-ingang).</summary>
    public void Attach(IMidiInput input)
    {
        Close();
        _input = input;
        input.Received += OnMessage;
    }

    public void Close()
    {
        var input = _input;
        _input = null;
        if (input is null) return;
        input.Received -= OnMessage;
        try { input.Dispose(); } catch { }
    }

    public void Dispose()
    {
        CancelLearn();
        Close();
    }

    // ── Berichten ─────────────────────────────────────────────────────────

    private void OnMessage(MidiMessage m)
    {
        MessageReceived?.Invoke(m);

        if (IsLearning)
        {
            Learn(m);
            return;
        }

        var s = _settings;
        if (s.Vfo is { } vfo && vfo.Matches(m) && m.Kind == MidiKind.ControlChange)
        {
            int clicks = s.Encoder.Decode(m.Value);
            if (clicks != 0) _rig.TuneBy((long)clicks * _stepHz());
            return;
        }

        if (s.Ptt is { } ptt && ptt.Matches(m))
        {
            if (s.PttMode == PttMode.Hold)
            {
                if (m.IsPress) _ = _rig.SetPttAsync(true);
                else if (m.IsRelease) _ = _rig.SetPttAsync(false);
            }
            else if (m.IsPress)
            {
                _ = _rig.SetPttAsync(!_rig.State.Ptt);
            }
        }
    }

    // ── Leren ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Hoort dit CC bericht bij de (geleerde) VFO-knop of het nummer ernaast?
    /// Sommige controllers sturen per klik twee CC's (bijv. CC 64 = 64/66 en CC 65 = 1/127).
    /// </summary>
    private bool IsEncoderLike(MidiMessage m) =>
        _settings.Vfo is { } vfo && vfo.Kind == MidiKind.ControlChange && m.Channel == vfo.Channel
        && Math.Abs(m.Number - vfo.Number) <= 1;

    /// <summary>VFO leren: eerst een paar klikjes rechtsom, dan linksom.</summary>
    public void LearnVfo() => StartLearn(LearnPhase.VfoRight, "Draai de VFO-knop een paar klikjes RECHTSOM…");

    /// <summary>PTT leren: druk één keer op de knop.</summary>
    public void LearnPtt() => StartLearn(LearnPhase.Ptt, "Druk op de PTT-knop…");

    public void CancelLearn()
    {
        lock (_lock)
        {
            _phase = LearnPhase.None;
            _learnTimer?.Dispose();
            _learnTimer = null;
        }
    }

    private void StartLearn(LearnPhase phase, string prompt)
    {
        lock (_lock)
        {
            if (_input is null)
            {
                LearnFinished?.Invoke(null, "Kies eerst een MIDI apparaat.");
                return;
            }
            _phase = phase;
            _learnControl = null;
            _right.Clear();
            _left.Clear();
            _learnStarted = Environment.TickCount64;
            _lastLearnMsg = 0;
            _learnTimer?.Dispose();
            _learnTimer = new Timer(_ => LearnTick(), null, 200, 200);
        }
        LearnProgress?.Invoke(prompt);
    }

    private void Learn(MidiMessage m)
    {
        MidiSettings? done = null;
        string? doneMessage = null;
        string? progress = null;

        lock (_lock)
        {
            switch (_phase)
            {
                // De VFO-knop (of zijn tweede CC) is geen PTT: draaien tijdens PTT-leren negeren
                case LearnPhase.Ptt when m.IsPress && !(m.Kind == MidiKind.ControlChange && IsEncoderLike(m)):
                    done = _settings.Clone();
                    done.Ptt = MidiControl.From(m);
                    doneMessage = $"PTT geleerd: {done.Ptt}";
                    _phase = LearnPhase.None;
                    break;

                case LearnPhase.VfoRight or LearnPhase.VfoLeft when m.Kind == MidiKind.ControlChange:
                    _learnControl ??= MidiControl.From(m);
                    if (!_learnControl.Matches(m)) break; // andere knop: negeren
                    (_phase == LearnPhase.VfoRight ? _right : _left).Add(m.Value);
                    _lastLearnMsg = Environment.TickCount64;
                    progress = _phase == LearnPhase.VfoRight
                        ? $"Rechtsom: {string.Join(" ", _right.TakeLast(12))}"
                        : $"Linksom: {string.Join(" ", _left.TakeLast(12))}";
                    break;
            }
            if (_phase == LearnPhase.None) { _learnTimer?.Dispose(); _learnTimer = null; }
        }

        if (progress is not null) LearnProgress?.Invoke(progress);
        if (done is not null) LearnFinished?.Invoke(done, doneMessage!);
    }

    /// <summary>Fase wisselen zodra de knop even stilstaat (na minstens 3 berichten).</summary>
    private void LearnTick()
    {
        MidiSettings? done = null;
        string? message = null;
        string? progress = null;
        bool failed = false;

        lock (_lock)
        {
            if (_phase == LearnPhase.None) return;
            var now = Environment.TickCount64;

            if (now - _learnStarted > 20_000)
            {
                failed = true;
                message = _phase == LearnPhase.Ptt ? "Geen knop ontvangen." : "Te weinig ontvangen van de VFO-knop.";
                _phase = LearnPhase.None;
            }
            else if (_lastLearnMsg != 0 && now - _lastLearnMsg > 700)
            {
                if (_phase == LearnPhase.VfoRight && _right.Count >= 3)
                {
                    _phase = LearnPhase.VfoLeft;
                    _lastLearnMsg = 0;
                    progress = $"Rechtsom: {string.Join(" ", _right.TakeLast(12))}   — draai nu een paar klikjes LINKSOM…";
                }
                else if (_phase == LearnPhase.VfoLeft && _left.Count >= 3)
                {
                    var result = EncoderLearner.Infer(_right, _left);
                    message = $"{_learnControl}: {result.Message}";
                    if (result.Settings is { } enc)
                    {
                        done = _settings.Clone();
                        done.Vfo = _learnControl;
                        done.Encoder = enc;
                    }
                    else failed = true;
                    _phase = LearnPhase.None;
                }
            }
            if (_phase == LearnPhase.None) { _learnTimer?.Dispose(); _learnTimer = null; }
        }

        if (progress is not null) LearnProgress?.Invoke(progress);
        if (done is not null) LearnFinished?.Invoke(done, message!);
        else if (failed) LearnFinished?.Invoke(null, message!);
    }
}
