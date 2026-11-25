using System.Globalization;
using BoltCat.Core;

// BoltCat.Test — BoltCat.Core testen tegen een echte radio.
//
//   BoltCat.Test --rig ft-897d --com COM3 --baud 4800    start zelf rigctld (meegeleverd)
//   BoltCat.Test                                         verbind met een rigctld die al draait
//   BoltCat.Test --list 897                              modellen zoeken
//
// Toont live wat de radio doet en accepteert commando's (typ 'help').

string host = "127.0.0.1";
int port = RigctldProtocol.DefaultPort;
bool vfoMode = false;
string? rig = null, com = null, rigctldPath = null, listQuery = null;
int? baud = null;
bool list = false;

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} verwacht een waarde");
    try
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--rig":     rig = Next(); break;
            case "--com":     com = Next(); break;
            case "--baud":    baud = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--rigctld": rigctldPath = Next(); break;
            case "--host":    host = Next(); break;
            case "--port":    port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--vfo":     vfoMode = true; break;
            case "--list":
                list = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) listQuery = args[++i];
                break;
            case "--help" or "-h" or "/?":
                Console.WriteLine("""
                    BoltCat.Test — CAT test via rigctld

                    Gebruik:
                      BoltCat.Test --rig <model> --com <poort> [--baud <baud>]
                      BoltCat.Test [--host 127.0.0.1] [--port 4532]     (rigctld draait al)
                      BoltCat.Test --list [zoekterm]

                      --rig <model>     Hamlib model: nummer of naam, bijv. ft-897d, ft-950, ic-7300
                      --com <poort>     COM3 (Windows) of /dev/ttyUSB0 (Linux)
                      --baud <baud>     zelfde als de CAT RATE in het menu van de radio
                      --vfo             rigctld met VFO-modus (VFO B uitlezen) — niet bij FT-817/857/897
                      --rigctld <pad>   ander rigctld gebruiken dan het meegeleverde
                      --list [term]     radio's opzoeken, bijv. --list 897

                    Voorbeeld FT-897D:
                      BoltCat.Test --rig ft-897d --com COM3 --baud 4800
                    """);
                return 0;
            default:
                Console.Error.WriteLine($"Onbekende optie: {args[i]} (zie --help)");
                return 1;
        }
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

// ── Modellen opzoeken ─────────────────────────────────────────────────────
if (list || (rig is not null && !int.TryParse(rig, out _)))
{
    var exe = RigctldProcess.FindExecutable(rigctldPath);
    if (exe is null)
    {
        Console.Error.WriteLine("rigctld niet gevonden (map 'hamlib' naast BoltCat.Test, of --rigctld <pad>).");
        return 1;
    }
    var models = await RigctldProcess.ListModelsAsync(exe);

    if (list)
    {
        var shown = listQuery is null ? models : RigctldProcess.FindModels(models, listQuery);
        foreach (var m in shown) Console.WriteLine(m);
        Console.WriteLine($"{shown.Count} radio's");
        return 0;
    }

    var found = RigctldProcess.FindModels(models, rig!);
    static string N(string x) => new string(x.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    bool exact = found.Count > 0 && (N(found[0].Model) == N(rig!) || N(found[0].Manufacturer + found[0].Model) == N(rig!));
    if (found.Count == 0)
    {
        Console.Error.WriteLine($"Geen radio gevonden voor '{rig}'. Zoek met --list <term>.");
        return 1;
    }
    if (found.Count > 1 && !exact)
    {
        Console.Error.WriteLine($"Meerdere radio's voor '{rig}', kies er een met --rig <nummer> of de volledige naam:");
        foreach (var m in found) Console.Error.WriteLine(m);
        return 1;
    }
    rig = found[0].Number.ToString(CultureInfo.InvariantCulture);
    Console.WriteLine($"Radio: {found[0]}");
}

// ── rigctld starten ───────────────────────────────────────────────────────
RigctldProcess? rigctld = null;
if (rig is not null)
{
    rigctld = new RigctldProcess(new RigctldOptions
    {
        Model = int.Parse(rig, CultureInfo.InvariantCulture),
        SerialPort = com,
        Baud = baud,
        ListenHost = host,
        TcpPort = port,
        VfoMode = vfoMode,
        Executable = rigctldPath,
    });
    rigctld.Output += l => Console.WriteLine($"  rigctld> {l}");
    try
    {
        await rigctld.StartAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FOUT: {ex.Message}");
        return 1;
    }

    // Ctrl+C: rigctld niet achterlaten
    Console.CancelKeyPress += (_, _) =>
    {
        try { rigctld.StopAsync().GetAwaiter().GetResult(); } catch { }
    };
}

Console.WriteLine($"BoltCat.Test — rigctld op {host}:{port}{(vfoMode ? " (VFO-modus)" : "")}");
Console.WriteLine("Typ 'help' voor commando's, 'q' om te stoppen.\n");

await using var client = new RigctldClient(host, port, vfoMode);
await using var poller = new RigPoller(client);

// Alleen een nieuwe regel als frequentie, mode, PTT, split of verbinding verandert.
// De S-meter (verandert continu) staat in de venstertitel, anders kun je niet typen.
RigState? lastPrinted = null;
bool trace = false;
poller.StateChanged += s =>
{
    try { Console.Title = $"BoltCat.Test — {Format(s)}"; } catch { /* geen console titel (Linux/redirect) */ }
    if (lastPrinted is not null && (lastPrinted with { StrengthDb = s.StrengthDb }) == s) return;
    lastPrinted = s;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Format(s)}");
};
client.Trace += (c, lines) =>
{
    if (trace) Console.WriteLine($"  >> {c}   << {string.Join(" | ", lines)}");
};
poller.Notice       += n => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ! {n}");
poller.Start();

while (true)
{
    var line = Console.ReadLine();
    if (line is null) break;
    var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (p.Length == 0) continue;

    var cmd = p[0].ToLowerInvariant();
    if (cmd is "q" or "quit" or "exit") break;

    try
    {
        switch (cmd)
        {
            case "help" or "?":
                Console.WriteLine("""
                      f <freq>        frequentie, in Hz (7074000) of MHz (7.074)
                      m <mode> [pb]   mode: USB LSB CW CWR AM FM PKTUSB PKTLSB PKTFM ...  (pb = passband Hz, 0 = standaard)
                      tx / rx         PTT aan / uit
                      split on|off    split aan / uit (TX op VFO B)
                      toggle          VFO A/B wisselen   (FT-817/857/897)
                      ab              A<->B omwisselen   (niet op FT-897)
                      a2b             A -> B kopieren    (niet op FT-897)
                      vfo a|b         VFO kiezen
                      s               huidige toestand tonen
                      bench           meet per CAT-commando hoe lang de radio erover doet
                      trace           ruwe rigctld commando's en antwoorden tonen (aan/uit)
                      cache [ms]      Hamlib cache tonen of instellen (BoltCat zet hem op 100 ms)
                      raw <cmd>       ruw rigctld commando, bijv. 'raw get_level SWR' of 'raw dump_caps'
                      q               stoppen (PTT gaat eerst uit)
                    """);
                break;

            case "f" when p.Length > 1:
                await client.SetFrequencyAsync(ParseFreq(p[1]));
                break;

            case "m" when p.Length > 1:
                await client.SetModeAsync(p[1], p.Length > 2 ? int.Parse(p[2], CultureInfo.InvariantCulture) : 0);
                break;

            case "tx":
                await client.SetPttAsync(true);
                break;

            case "rx":
                await client.SetPttAsync(false);
                break;

            case "split" when p.Length > 1:
                await client.SetSplitAsync(p[1].Equals("on", StringComparison.OrdinalIgnoreCase));
                break;

            case "toggle": await client.VfoOpAsync(VfoOp.Toggle); break;
            case "ab":     await client.VfoOpAsync(VfoOp.Swap); break;
            case "a2b":    await client.VfoOpAsync(VfoOp.CopyAtoB); break;

            case "vfo" when p.Length > 1:
                await client.SetVfoAsync(p[1].Equals("b", StringComparison.OrdinalIgnoreCase) ? Vfo.B : Vfo.A);
                break;

            case "s":
                Console.WriteLine($"  {Format(poller.State)}");
                break;

            case "bench":
                await Bench(client);
                break;

            case "trace":
                trace = !trace;
                Console.WriteLine($"  trace {(trace ? "aan" : "uit")}");
                break;

            case "cache":
                if (p.Length > 1) await client.SetCacheTimeoutAsync(int.Parse(p[1], CultureInfo.InvariantCulture));
                Console.WriteLine($"  Hamlib cache: {await client.GetCacheTimeoutAsync()} ms");
                break;

            case "raw" when p.Length > 1:
                var r = await client.SendAsync(string.Join(' ', p[1..]));
                foreach (var kv in r.Values)
                    Console.WriteLine(kv.Key.Length > 0 ? $"  {kv.Key}: {kv.Value}" : $"  {kv.Value}");
                Console.WriteLine($"  RPRT {r.Code} ({RigctldProtocol.ErrorText(r.Code)})");
                break;

            default:
                Console.WriteLine("Onbekend commando, typ 'help'.");
                break;
        }
        poller.RefreshNow();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FOUT: {ex.Message}");
    }
}

// Veiligheid: nooit in TX achterlaten
if (client.IsConnected)
{
    try { await client.SetPttAsync(false); } catch { }
}
await poller.StopAsync();
if (rigctld is not null) await rigctld.DisposeAsync();
return 0;

// Elk leescommando 5x, tijd per keer. Laat zien welk commando de poller ophoudt.
static async Task Bench(RigctldClient client)
{
    // In VFO-modus komt "currVFO" direct na de commandonaam
    string V(string c)
    {
        if (!client.VfoMode) return c;
        int sp = c.IndexOf(' ');
        return sp < 0 ? c + " currVFO" : c[..sp] + " currVFO" + c[sp..];
    }
    string[] cmds = ["get_freq", "get_mode", "get_ptt", "get_level STRENGTH", "get_split_vfo", "get_vfo"];
    Console.WriteLine("  commando              gem.    max   resultaat");
    long total = 0;
    foreach (var c in cmds)
    {
        var cmd = c == "get_vfo" ? c : V(c);
        var times = new List<long>();
        int code = 0;
        string? err = null;
        for (int i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { code = (await client.SendAsync(cmd)).Code; }
            catch (Exception ex) { err = ex.Message; if (!client.IsConnected) await client.ConnectAsync(); }
            times.Add(sw.ElapsedMilliseconds);
        }
        var avg = (long)times.Average();
        if (c is "get_freq" or "get_mode" or "get_ptt" or "get_level STRENGTH") total += avg;
        Console.WriteLine($"  {c,-20} {avg,5} ms {times.Max(),5} ms  {err ?? $"RPRT {code} ({RigctldProtocol.ErrorText(code)})"}");
    }
    Console.WriteLine($"  → één poll-ronde (freq+mode+ptt+S-meter) ≈ {total} ms");
}

static long ParseFreq(string s)
{
    var v = double.Parse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
    return (long)Math.Round(v < 1_000 ? v * 1_000_000 : v); // < 1000 → MHz
}

static string Format(RigState s)
{
    if (!s.Connected)
        return $"niet verbonden — {s.Error ?? "wachten op rigctld"}";

    var f = s.FrequencyA is { } hz ? $"{hz / 1_000_000.0:0.000000} MHz" : "? MHz";
    var b = s.FrequencyB is { } hzB ? $"  B {hzB / 1_000_000.0:0.000000}" : "";
    var meter = s.StrengthDb is { } db ? $"{SMeter.Format(db),-6} ({db,4} dB)" : "S-meter n.v.t.";
    var split = s.Split ? $"  SPLIT (TX {s.TxVfo})" : "";
    var pb = s.Passband is > 0 ? $"{s.Passband,5} Hz" : "        "; // 0 = standaardfilter van de radio
    var mode = string.IsNullOrEmpty(s.Mode) ? "?" : s.Mode; // leeg = Hamlib kent deze mode niet
    return $"{f}{b}  {mode,-6} {pb}  {(s.Ptt ? "TX" : "RX")}  {meter}{split}";
}
