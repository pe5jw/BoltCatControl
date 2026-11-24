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
    private long _cycle;

    public RigPoller(RigctldClient client) => _client = client;

    /// <summary>Tijd tussen twee uitleesrondes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Wachttijd voor een nieuwe verbindingspoging.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Split en VFO B worden elke zoveelste ronde gelezen (minder CAT verkeer).</summary>
    public int SlowEvery { get; set; } = 5;

    public RigState State { get; private set; } = RigState.Disconnected();

    public event Action<RigState>? StateChanged;

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
                    _cycle = 0;
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
            try { strength = await _client.GetStrengthAsync(ct).ConfigureAwait(false); }
            catch (RigctldException) { _strengthSupported = false; }
        }

        bool split = prev.Split;
        Vfo txVfo = prev.TxVfo;
        long? freqB = prev.FrequencyB;
        if (full)
        {
            if (_splitSupported)
            {
                try { (split, txVfo) = await _client.GetSplitAsync(ct).ConfigureAwait(false); }
                catch (RigctldException) { _splitSupported = false; split = false; }
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
