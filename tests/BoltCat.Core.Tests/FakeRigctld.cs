using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BoltCat.Core.Tests;

/// <summary>
/// Minimale nep-rigctld voor tests: luistert op een vrije poort op 127.0.0.1 en
/// beantwoordt extended commando's (<c>+\get_freq</c>) zoals Hamlib rigctld dat doet.
/// </summary>
internal sealed class FakeRigctld : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _accept;

    public long FreqA = 14_074_000;
    public long FreqB = 7_074_000;
    public string Mode = "USB";
    public int Passband = 2400;
    public bool Ptt;
    public int Strength = -12;
    public bool Split;
    public string TxVfo = "VFOA";
    public bool StrengthSupported = true;

    /// <summary>Foutcode voor get_level (0 = OK), bijv. -5 om een tijdelijke radio-timeout te simuleren.</summary>
    public volatile int StrengthCode;

    /// <summary>Commando's waar niet op geantwoord wordt (timeout simuleren).</summary>
    public readonly ConcurrentDictionary<string, bool> Mute = new();

    public readonly ConcurrentQueue<string> Received = new();

    public FakeRigctld()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                lock (_clients) _clients.Add(c);
                _ = Task.Run(() => Serve(c));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task Serve(TcpClient c)
    {
        try
        {
            using var stream = c.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                Received.Enqueue(line);
                var cmd = line.TrimStart('+').TrimStart('\\');
                var name = cmd.Split(' ')[0];
                if (Mute.ContainsKey(name)) continue;
                await writer.WriteAsync(Respond(cmd));
            }
        }
        catch { /* verbinding dicht */ }
    }

    private string Respond(string cmd)
    {
        var p = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string Ok(string echo) => $"{echo}\nRPRT 0\n";
        string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

        switch (p[0])
        {
            case "get_freq":
                var vfo = p.Length > 1 ? p[1] : "currVFO";
                return $"get_freq: {vfo}\nFrequency: {Inv(vfo == "VFOB" ? FreqB : FreqA)}\nRPRT 0\n";
            case "set_freq":
                if (p.Length == 3) { if (p[1] == "VFOB") FreqB = long.Parse(p[2]); else FreqA = long.Parse(p[2]); }
                else FreqA = long.Parse(p[1], CultureInfo.InvariantCulture);
                return Ok($"set_freq: {string.Join(' ', p[1..])}");
            case "get_mode":
                return $"get_mode:\nMode: {Mode}\nPassband: {Passband}\nRPRT 0\n";
            case "set_mode":
                Mode = p[^2];
                Passband = int.Parse(p[^1], CultureInfo.InvariantCulture);
                return Ok($"set_mode: {Mode} {Passband}");
            case "get_ptt":
                return $"get_ptt:\nPTT: {(Ptt ? 1 : 0)}\nRPRT 0\n";
            case "set_ptt":
                Ptt = p[^1] != "0";
                return Ok($"set_ptt: {p[^1]}");
            case "get_level":
                if (StrengthCode != 0) return $"get_level: STRENGTH\nRPRT {StrengthCode}\n";
                return StrengthSupported
                    ? $"get_level: STRENGTH\n{Strength}\nRPRT 0\n"
                    : "get_level: STRENGTH\nRPRT -11\n";
            case "get_split_vfo":
                return $"get_split_vfo:\nSplit: {(Split ? 1 : 0)}\nTX VFO: {TxVfo}\nRPRT 0\n";
            case "set_split_vfo":
                Split = p[^2] != "0";
                TxVfo = p[^1];
                return Ok($"set_split_vfo: {p[^2]} {p[^1]}");
            case "vfo_op":
                if (p[^1] == "XCHG") (FreqA, FreqB) = (FreqB, FreqA);
                else if (p[^1] == "CPY") FreqB = FreqA;
                else if (p[^1] == "TOGGLE") (FreqA, FreqB) = (FreqB, FreqA);
                else return "vfo_op:\nRPRT -1\n";
                return Ok($"vfo_op: {p[^1]}");
            case "get_vfo":
                return "get_vfo:\nVFO: VFOA\nRPRT 0\n";
            default:
                return $"{p[0]}:\nRPRT -4\n";
        }
    }

    /// <summary>Alle open verbindingen hard verbreken (rigctld gecrasht).</summary>
    public void DropClients()
    {
        lock (_clients)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        DropClients();
        try { await _accept; } catch { }
        _cts.Dispose();
    }
}
