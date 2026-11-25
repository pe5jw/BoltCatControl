using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace BoltCat.Core;

/// <summary>
/// TCP client voor een lokale Hamlib rigctld (standaard 127.0.0.1:4532).
///
/// Gebruikt het extended response protocol (<c>+\get_freq</c>): elk antwoord eindigt
/// met een <c>RPRT n</c> regel, zodat we altijd weten waar een antwoord ophoudt.
/// Commando's worden na elkaar afgehandeld (één tegelijk), dus de poller en de GUI
/// kunnen dezelfde client gebruiken.
///
/// <see cref="VfoMode"/>: zet op true als rigctld met <c>--vfo</c> is gestart.
/// Dan krijgt elk commando een VFO argument en kan VFO B uitgelezen worden
/// zonder dat de radio van VFO wisselt.
/// </summary>
public sealed class RigctldClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public RigctldClient(string host = "127.0.0.1", int port = RigctldProtocol.DefaultPort, bool vfoMode = false)
    {
        Host = host;
        Port = port;
        VfoMode = vfoMode;
    }

    public string Host { get; }
    public int Port { get; }
    public bool VfoMode { get; }

    /// <summary>Maximale wachttijd per commando (inclusief de reactie van de radio).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    public bool IsConnected => _tcp?.Connected == true && _writer is not null;

    /// <summary>Elk commando met de ruwe antwoordregels (voor diagnose).</summary>
    public event Action<string, IReadOnlyList<string>>? Trace;

    // ── Verbinding ────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Drop();
            var tcp = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                await tcp.ConnectAsync(Host, Port, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new TimeoutException($"Geen verbinding met rigctld op {Host}:{Port}");
            }
            catch
            {
                tcp.Dispose();
                throw;
            }

            var stream = tcp.GetStream();
            _reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            _tcp = tcp;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { Drop(); }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _lock.Dispose();
    }

    private void Drop()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _tcp?.Dispose();
        _reader = null;
        _writer = null;
        _tcp = null;
    }

    // ── Ruw commando ──────────────────────────────────────────────────────

    /// <summary>
    /// Stuur een rigctld commando (lange naam zonder backslash, bijv. <c>get_freq</c>)
    /// en lees het antwoord tot en met de RPRT regel. Gooit geen exception bij RPRT &lt; 0;
    /// gebruik daarvoor de getypeerde methodes of <see cref="Ensure"/>.
    /// </summary>
    public async Task<RigctldResponse> SendAsync(string command, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_writer is null || _reader is null)
                throw new InvalidOperationException("Niet verbonden met rigctld");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            await _writer.WriteLineAsync(("+\\" + command).AsMemory(), cts.Token).ConfigureAwait(false);

            var lines = new List<string>(4);
            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false)
                           ?? throw new IOException("rigctld heeft de verbinding gesloten");
                lines.Add(line);
                if (line.StartsWith("RPRT", StringComparison.Ordinal))
                    break;
            }
            Trace?.Invoke(command, lines);

            // Controleren of dit antwoord bij ons commando hoort. In extended modus begint
            // het antwoord met de naam van het commando ("get_freq:"). Zo niet, dan loopt
            // de stream uit de pas (bijv. door een onbekend commando) → opnieuw verbinden.
            if (!RigctldProtocol.EchoMatches(command, lines))
            {
                Drop();
                throw new IOException($"Antwoord van rigctld hoort niet bij '{command}' (kreeg '{lines[0]}') — opnieuw verbinden");
            }
            return RigctldProtocol.Parse(lines);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Antwoord kwam niet op tijd: de stream is nu niet meer in de pas → verbinding opnieuw opbouwen
            Drop();
            throw new TimeoutException($"rigctld reageert niet op '{command}'");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Drop();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<RigctldResponse> Ensure(string command, CancellationToken ct)
    {
        var r = await SendAsync(command, ct).ConfigureAwait(false);
        if (!r.Ok) throw new RigctldException(command, r.Code);
        return r;
    }

    /// <summary>Commando met (in VFO-modus) het VFO argument ervoor.</summary>
    private string Cmd(string name, Vfo vfo, string args = "")
    {
        var sb = new StringBuilder(name);
        if (VfoMode) sb.Append(' ').Append(RigctldProtocol.VfoName(vfo));
        if (args.Length > 0) sb.Append(' ').Append(args);
        return sb.ToString();
    }

    private static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Inv(int v)  => v.ToString(CultureInfo.InvariantCulture);

    // ── Frequentie ────────────────────────────────────────────────────────

    public async Task<long> GetFrequencyAsync(Vfo vfo = Vfo.Current, CancellationToken ct = default)
    {
        var r = await Ensure(Cmd("get_freq", vfo), ct).ConfigureAwait(false);
        return RigctldProtocol.ParseFrequency(r.Value());
    }

    public Task SetFrequencyAsync(long hz, Vfo vfo = Vfo.Current, CancellationToken ct = default)
    {
        if (hz <= 0) throw new ArgumentOutOfRangeException(nameof(hz));
        return Ensure(Cmd("set_freq", vfo, Inv(hz)), ct);
    }

    // ── Mode ──────────────────────────────────────────────────────────────

    /// <summary>Mode (USB, LSB, CW, AM, FM, PKTUSB, ...) en passband in Hz.</summary>
    public async Task<(string Mode, int Passband)> GetModeAsync(Vfo vfo = Vfo.Current, CancellationToken ct = default)
    {
        var r = await Ensure(Cmd("get_mode", vfo), ct).ConfigureAwait(false);
        var mode = r.Value(0) ?? throw new FormatException("Geen mode in antwoord van rigctld");
        var pb = r.Value(1) is { } p ? RigctldProtocol.ParseInt(p) : 0;
        return (mode, pb);
    }

    /// <summary>Mode zetten. Passband 0 = standaard filter van de radio, -1 = filter niet wijzigen.</summary>
    public Task SetModeAsync(string mode, int passband = 0, Vfo vfo = Vfo.Current, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        return Ensure(Cmd("set_mode", vfo, $"{mode.Trim().ToUpperInvariant()} {Inv(passband)}"), ct);
    }

    // ── PTT ───────────────────────────────────────────────────────────────

    public async Task<bool> GetPttAsync(CancellationToken ct = default)
    {
        var r = await Ensure(Cmd("get_ptt", Vfo.Current), ct).ConfigureAwait(false);
        return RigctldProtocol.ParseInt(r.Value()) != 0;
    }

    public Task SetPttAsync(bool tx, CancellationToken ct = default) =>
        Ensure(Cmd("set_ptt", Vfo.Current, tx ? "1" : "0"), ct);

    // ── VFO / split ───────────────────────────────────────────────────────

    public async Task<Vfo> GetVfoAsync(CancellationToken ct = default)
    {
        // get_vfo heeft geen VFO argument, ook niet in VFO-modus
        var r = await Ensure("get_vfo", ct).ConfigureAwait(false);
        return RigctldProtocol.ParseVfo(r.Value());
    }

    public Task SetVfoAsync(Vfo vfo, CancellationToken ct = default)
    {
        if (vfo == Vfo.Current) throw new ArgumentException("Kies VFO A of B", nameof(vfo));
        return Ensure($"set_vfo {RigctldProtocol.VfoName(vfo)}", ct);
    }

    public async Task<(bool Split, Vfo TxVfo)> GetSplitAsync(CancellationToken ct = default)
    {
        var r = await Ensure(Cmd("get_split_vfo", Vfo.Current), ct).ConfigureAwait(false);
        return (RigctldProtocol.ParseInt(r.Value(0)) != 0, RigctldProtocol.ParseVfo(r.Value(1)));
    }

    public Task SetSplitAsync(bool split, Vfo txVfo = Vfo.B, CancellationToken ct = default) =>
        Ensure(Cmd("set_split_vfo", Vfo.Current, $"{(split ? 1 : 0)} {RigctldProtocol.VfoName(txVfo == Vfo.Current ? Vfo.B : txVfo)}"), ct);

    public Task VfoOpAsync(VfoOp op, CancellationToken ct = default) =>
        Ensure(Cmd("vfo_op", Vfo.Current, RigctldProtocol.VfoOpName(op)), ct);

    // ── Hamlib cache ──────────────────────────────────────────────────────

    /// <summary>
    /// Hoe lang rigctld een uitgelezen waarde (freq, mode, ...) hergebruikt voordat hij
    /// de radio opnieuw vraagt. Standaard vaak 1000 ms — te traag voor een VFO knop.
    /// </summary>
    public async Task<int> GetCacheTimeoutAsync(CancellationToken ct = default)
    {
        var r = await Ensure("get_cache", ct).ConfigureAwait(false);
        return RigctldProtocol.ParseInt(r.Value());
    }

    /// <summary>Cache timeout in ms, 0 = cache uit.</summary>
    public Task SetCacheTimeoutAsync(int milliseconds, CancellationToken ct = default) =>
        Ensure($"set_cache {Inv(Math.Max(0, milliseconds))}", ct);

    // ── S-meter ───────────────────────────────────────────────────────────

    /// <summary>Signaalsterkte in dB t.o.v. S9 (S9 = 0, S0 ≈ -54, S9+20 = 20).</summary>
    public async Task<int> GetStrengthAsync(CancellationToken ct = default)
    {
        var r = await Ensure(Cmd("get_level", Vfo.Current, "STRENGTH"), ct).ConfigureAwait(false);
        return RigctldProtocol.ParseInt(r.Value());
    }
}
