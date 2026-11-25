namespace BoltCat.Core;

/// <summary>
/// Leest de radio periodiek uit via <see cref="RigctldClient"/> en meldt wijzigingen.
///
///  • elke cyclus: frequentie, mode, PTT, S-meter
///  • elke <see cref="SlowEvery"/>e cyclus: split en VFO B
///  • verbinding weg → <see cref="RigState.Disconnected"/> melden en opnieuw verbinden
///
/// <see cref="StateChanged"/> komt op een achtergrondthread binnen; de GUI moet zelf
/// naar de UI-thread marshallen.
/// </summary>
public sealed class RigPoller : IAsyncDisposable
{
    private readonly RigctldClient _client;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _strengthSupported = true;
    private bool _splitSupported = true;
    private bool _strengthFailing;
    private bool _splitFailing;
    private long _cycle;

    public RigPoller(RigctldClient client) => _client = client;

    /// <summary>Tijd tussen twee uitleesrondes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Wachttijd voor een nieuwe verbindingspoging.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Hamlib cache timeout die na het verbinden wordt ingesteld (rigctld staat vaak op
    /// 1000 ms, waardoor de frequentie maar 1x per seconde verandert). Null = niet wijzigen.
    /// </summary>
    public TimeSpan? CacheTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Split en VFO B worden elke zoveelste ronde gelezen (minder CAT verkeer).</summary>
    public int SlowEvery { get; set; } = 5;

    public RigState State { get; private set; } = RigState.Disconnected();

    public event Action<RigState>? StateChanged;

    /// <summary>Diagnostische meldingen, bijv. "S-meter niet ondersteund" of een tijdelijke CAT fout.</summary>
    public event Action<string>? Notice;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loop is null) return;
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>Direct opnieuw uitlezen, bijv. na een set-commando vanuit de GUI.</summary>
    public void RefreshNow()
    {
        Interlocked.Exchange(ref _cycle, -1); // volgende ronde is een volledige ronde
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wake.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await _client.ConnectAsync(ct).ConfigureAwait(false);
                    _strengthSupported = true;
                    _splitSupported = true;
                    _strengthFailing = false;
                    _splitFailing = false;
                    _cycle = 0;
                    await ApplyCacheTimeoutAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Publish(RigState.Disconnected(ex.Message));
                    await Delay(ReconnectDelay, ct).ConfigureAwait(false);
                    continue;
                }
            }

            try
            {
                var cycle = Interlocked.Increment(ref _cycle);
                var full = cycle <= 0 || cycle % Math.Max(1, SlowEvery) == 1 || SlowEvery <= 1;
                Publish(await ReadAsync(full, ct).ConfigureAwait(false));
                await Delay(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // rigctld weg of radio reageert niet: opnieuw verbinden
                Publish(RigState.Disconnected(ex.Message));
                await _client.DisconnectAsync().ConfigureAwait(false);
                await Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyCacheTimeoutAsync(CancellationToken ct)
    {
        if (CacheTimeout is not { } t) return;
        var ms = (int)t.TotalMilliseconds;
        try
        {
            var before = await _client.GetCacheTimeoutAsync(ct).ConfigureAwait(false);
            if (before != ms)
            {
                await _client.SetCacheTimeoutAsync(ms, ct).ConfigureAwait(false);
                Notice?.Invoke($"Hamlib cache {before} ms → {ms} ms");
            }
        }
        catch (RigctldException ex)
        {
            Notice?.Invoke($"Hamlib cache instellen niet ondersteund ({ex.Message})");
        }
    }

    private async Task<RigState> ReadAsync(bool full, CancellationToken ct)
    {
        var prev = State;
        var vfoA = _client.VfoMode ? Vfo.A : Vfo.Current;

        var freqA = await _client.GetFrequencyAsync(vfoA, ct).ConfigureAwait(false);
        var (mode, pb) = await _client.GetModeAsync(vfoA, ct).ConfigureAwait(false);
        var ptt = await _client.GetPttAsync(ct).ConfigureAwait(false);

        int? strength = null;
        if (_strengthSupported)
        {
            try
            {
                strength = await _client.GetStrengthAsync(ct).ConfigureAwait(false);
                if (_strengthFailing) Notice?.Invoke("S-meter werkt weer");
                _strengthFailing = false;
            }
            catch (RigctldException ex) when (IsUnsupported(ex))
            {
                _strengthSupported = false;
                Notice?.Invoke($"S-meter niet ondersteund door deze radio ({ex.Message}) — wordt niet meer gevraagd");
            }
            catch (RigctldException ex)
            {
                // Tijdelijke fout (bijv. timeout naar de radio): vorige waarde houden, volgende ronde opnieuw
                strength = prev.StrengthDb;
                if (!_strengthFailing) Notice?.Invoke($"S-meter lezen mislukt, probeer opnieuw: {ex.Message}");
                _strengthFailing = true;
            }
        }

        bool split = prev.Split;
        Vfo txVfo = prev.TxVfo;
        long? freqB = prev.FrequencyB;
        if (full)
        {
            if (_splitSupported)
            {
                try
                {
                    (split, txVfo) = await _client.GetSplitAsync(ct).ConfigureAwait(false);
                    _splitFailing = false;
                }
                catch (RigctldException ex) when (IsUnsupported(ex))
                {
                    _splitSupported = false;
                    split = false;
                    Notice?.Invoke($"Split uitlezen niet ondersteund door deze radio ({ex.Message}) — wordt niet meer gevraagd");
                }
                catch (RigctldException ex)
                {
                    if (!_splitFailing) Notice?.Invoke($"Split lezen mislukt, probeer opnieuw: {ex.Message}");
                    _splitFailing = true;
                }
            }
            if (_client.VfoMode)
            {
                try { freqB = await _client.GetFrequencyAsync(Vfo.B, ct).ConfigureAwait(false); }
                catch (RigctldException) { freqB = null; }
            }
        }

        return new RigState
        {
            Connected  = true,
            FrequencyA = freqA,
            FrequencyB = freqB,
            Mode       = mode,
            Passband   = pb,
            Ptt        = ptt,
            Split      = split,
            TxVfo      = txVfo,
            StrengthDb = strength,
        };
    }

    /// <summary>
    /// Alleen deze Hamlib codes betekenen "kan deze radio niet": niet geïmplementeerd (-4),
    /// niet beschikbaar (-11) of ongeldige parameter (-1). Andere fouten (timeout -5, I/O -6,
    /// protocol -8) zijn tijdelijk en worden de volgende ronde opnieuw geprobeerd.
    /// </summary>
    internal static bool IsUnsupported(RigctldException ex) => ex.Code is -4 or -11 or -1;

    private void Publish(RigState state)
    {
        if (state == State) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>Wachten, maar eerder wakker worden bij <see cref="RefreshNow"/>.</summary>
    private async Task Delay(TimeSpan time, CancellationToken ct)
    {
        try { await _wake.WaitAsync(time, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }
}
