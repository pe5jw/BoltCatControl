namespace BoltCat.Core;

/// <summary>Alles wat nodig is om met de radio te verbinden.</summary>
public sealed record RigConnectionSettings
{
    /// <summary>Hamlib modelnummer (rigctld -l).</summary>
    public int Model { get; init; } = 1;
    public string? SerialPort { get; init; }
    public int? Baud { get; init; }
    public int TcpPort { get; init; } = RigctldProtocol.DefaultPort;

    /// <summary>rigctld --vfo: VFO B uitlezen zonder dat de radio wisselt. Niet voor FT-817/857/897.</summary>
    public bool VfoMode { get; init; }

    /// <summary>Hamlib cache in ms (null = niet wijzigen). 100 ms geeft vlot volgen van de VFO knop.</summary>
    public int? CacheMs { get; init; } = 100;

    /// <summary>Cache vanzelf verhogen als de radio het tempo niet bijhoudt.</summary>
    public bool AdaptiveCache { get; init; } = true;

    public string? RigctldPath { get; init; }
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}

/// <summary>
/// Eén aanspreekpunt voor de GUI (en later MIDI): start rigctld, verbindt, pollt en
/// voert commando's uit. Fouten worden gemeld via <see cref="Notice"/>, niet gegooid,
/// zodat een knop in de GUI nooit de app laat crashen.
///
/// Afstemmen (<see cref="TuneTo"/>) is "laatste wint": draai je snel aan de VFO, dan
/// wordt alleen de nieuwste frequentie naar de radio gestuurd. Bij een trage radio
/// (FT-897D ~150 ms per set_freq) loopt de VFO zo nooit achter.
/// </summary>
public sealed class RigController : IAsyncDisposable
{
    private readonly object _tuneLock = new();
    private RigctldProcess? _process;
    private RigctldClient? _client;
    private RigPoller? _poller;
    private long? _pendingFreq;
    private long? _localFreq;
    private long _holdUntil;
    private Task? _tuneTask;
    private bool _swapNeedsToggle;

    /// <summary>Hoe lang na een lokale VFO wijziging de poll-waarde genegeerd wordt (anti-terugspringen).</summary>
    public TimeSpan TuneHold { get; set; } = TimeSpan.FromMilliseconds(700);

    public RigState State { get; private set; } = RigState.Disconnected();
    public RigConnectionSettings? Settings { get; private set; }

    public bool IsConnecting { get; private set; }
    public bool IsActive => _poller is not null;
    public bool RigctldRunning => _process?.IsRunning == true || (_client?.IsConnected == true);
    public bool VfoBAvailable => Settings?.VfoMode == true;

    public event Action<RigState>? StateChanged;
    public event Action<string>? Notice;

    /// <summary>Uitvoer van rigctld (voor een logvenster / foutmelding).</summary>
    public event Action<string>? RigctldOutput;

    /// <summary>
    /// Frequentie om te tonen: tijdens het afstemmen de lokale waarde (direct, zonder te
    /// wachten op de radio), daarna weer wat de radio meldt.
    /// </summary>
    public long? DisplayFrequency
    {
        get
        {
            lock (_tuneLock)
            {
                if (_pendingFreq is { } p) return p;
                if (_localFreq is { } l && Environment.TickCount64 < _holdUntil) return l;
                return State.FrequencyA;
            }
        }
    }

    // ── Verbinden ─────────────────────────────────────────────────────────

    public async Task<bool> ConnectAsync(RigConnectionSettings settings, CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        Settings = settings;
        IsConnecting = true;
        Publish(RigState.Disconnected("Verbinden…"));

        try
        {
            var process = new RigctldProcess(new RigctldOptions
            {
                Model = settings.Model,
                SerialPort = settings.SerialPort,
                Baud = settings.Baud,
                TcpPort = settings.TcpPort,
                VfoMode = settings.VfoMode,
                ExtraArgs = settings.ExtraArgs,
                Executable = settings.RigctldPath,
            });
            process.Output += l => RigctldOutput?.Invoke(l);
            _process = process;
            await process.StartAsync(ct).ConfigureAwait(false);

            // Ruime timeout: een trage radio (of slechte verbinding) mag er even over doen
            var client = new RigctldClient("127.0.0.1", settings.TcpPort, settings.VfoMode) { Timeout = TimeSpan.FromSeconds(5) };
            var poller = new RigPoller(client)
            {
                CacheTimeout = settings.CacheMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                AdaptiveCache = settings.AdaptiveCache,
            };
            poller.StateChanged += Publish;
            poller.Notice += n => Notice?.Invoke(n);
            _client = client;
            _poller = poller;
            _swapNeedsToggle = false;
            poller.Start();
            return true;
        }
        catch (Exception ex)
        {
            var first = ex.Message.Split(Environment.NewLine)[0];
            Notice?.Invoke(first);
            await DisconnectAsync().ConfigureAwait(false);
            Publish(RigState.Disconnected(first));
            return false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task DisconnectAsync()
    {
        var poller = _poller;
        var client = _client;
        var process = _process;
        _poller = null;
        _client = null;
        _process = null;

        // Veiligheid: nooit in TX achterlaten
        if (client is { IsConnected: true } && State.Ptt)
        {
            try { await client.SetPttAsync(false).ConfigureAwait(false); } catch { }
        }
        if (poller is not null) await poller.DisposeAsync().ConfigureAwait(false);
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        if (process is not null) await process.DisposeAsync().ConfigureAwait(false);

        lock (_tuneLock) { _pendingFreq = null; _localFreq = null; }
        if (poller is not null || process is not null)
            Publish(RigState.Disconnected());
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private void Publish(RigState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    // ── Afstemmen ─────────────────────────────────────────────────────────

    /// <summary>Naar een frequentie afstemmen. Keert direct terug; versturen gaat op de achtergrond.</summary>
    public void TuneTo(long hz)
    {
        if (_client is null) return;
        hz = Math.Clamp(hz, 1_000, 999_999_999);
        lock (_tuneLock)
        {
            _pendingFreq = hz;
            _localFreq = hz;
            _holdUntil = Environment.TickCount64 + (long)TuneHold.TotalMilliseconds;
            if (_tuneTask is null || _tuneTask.IsCompleted)
                _tuneTask = Task.Run(SendPendingFrequencyAsync);
        }
        StateChanged?.Invoke(State); // GUI toont meteen de nieuwe frequentie
    }

    /// <summary>Afstemmen ten opzichte van de huidige frequentie (VFO knop, MIDI encoder).</summary>
    public void TuneBy(long deltaHz)
    {
        if (DisplayFrequency is { } f) TuneTo(f + deltaHz);
    }

    private async Task SendPendingFrequencyAsync()
    {
        while (true)
        {
            long hz;
            lock (_tuneLock)
            {
                if (_pendingFreq is not { } p) return;
                hz = p;
            }

            var client = _client;
            if (client is null) { lock (_tuneLock) _pendingFreq = null; return; }

            try
            {
                await client.SetFrequencyAsync(hz).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"Frequentie zetten mislukt: {ex.Message}");
            }

            lock (_tuneLock)
            {
                // Niets nieuws bijgekomen tijdens het versturen → klaar
                if (_pendingFreq == hz)
                {
                    _pendingFreq = null;
                    _holdUntil = Environment.TickCount64 + (long)TuneHold.TotalMilliseconds;
                }
            }
            _poller?.RefreshNow();
        }
    }

    // ── Bediening ─────────────────────────────────────────────────────────

    public Task SetModeAsync(string buttonOrHamlibMode) =>
        Run($"Mode {buttonOrHamlibMode}", c => c.SetModeAsync(RigModes.ToHamlib(buttonOrHamlibMode), 0));

    public Task SetPttAsync(bool tx) => Run(tx ? "PTT aan" : "PTT uit", c => c.SetPttAsync(tx));

    public Task SetSplitAsync(bool on) => Run(on ? "Split aan" : "Split uit", c => c.SetSplitAsync(on, Vfo.B));

    /// <summary>A↔B. Radio's zonder XCHG (FT-817/857/897) krijgen TOGGLE.</summary>
    public Task SwapVfoAsync() => Run("A↔B", async c =>
    {
        if (!_swapNeedsToggle)
        {
            try
            {
                await c.VfoOpAsync(VfoOp.Swap).ConfigureAwait(false);
                return;
            }
            catch (RigctldException ex) when (RigPoller.IsUnsupported(ex))
            {
                _swapNeedsToggle = true;
            }
        }
        await c.VfoOpAsync(VfoOp.Toggle).ConfigureAwait(false);
    });

    public Task CopyAtoBAsync() => Run("A→B", c => c.VfoOpAsync(VfoOp.CopyAtoB));

    private async Task Run(string what, Func<RigctldClient, Task> action)
    {
        var client = _client;
        if (client is null || !client.IsConnected)
        {
            Notice?.Invoke($"{what}: niet verbonden met de radio");
            return;
        }
        try
        {
            await action(client).ConfigureAwait(false);
        }
        catch (RigctldException ex) when (RigPoller.IsUnsupported(ex))
        {
            Notice?.Invoke($"{what}: niet ondersteund door deze radio");
        }
        catch (Exception ex)
        {
            Notice?.Invoke($"{what}: {ex.Message}");
        }
        _poller?.RefreshNow();
    }
}
