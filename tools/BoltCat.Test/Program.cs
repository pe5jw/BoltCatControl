using System.Globalization;
using BoltCat.Core;

// BoltCat.Test — BoltCat.Core testen tegen een echte rigctld + radio.
//
//   1. start rigctld:   rigctld -m <model> -r COM3 -s 4800
//   2. start dit:       BoltCat.Test [--host 127.0.0.1] [--port 4532] [--vfo]
//
// Toont live wat de radio doet en accepteert commando's (typ 'help').

string host = "127.0.0.1";
int port = RigctldProtocol.DefaultPort;
bool vfoMode = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--host" when i + 1 < args.Length: host = args[++i]; break;
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--vfo": vfoMode = true; break;
        case "--help" or "-h" or "/?":
            Console.WriteLine("""
                BoltCat.Test — CAT test via een lokale rigctld

                Gebruik: BoltCat.Test [--host 127.0.0.1] [--port 4532] [--vfo]

                  --vfo   alleen als rigctld met -o / --vfo is gestart
                          (VFO B uitlezen; niet gebruiken bij FT-817/857/897)

                Start eerst rigctld, bijvoorbeeld voor een FT-897D:
                  rigctl -l | findstr 897          (modelnummer opzoeken)
                  rigctld -m <model> -r COM3 -s 4800
                """);
            return 0;
        default:
            Console.Error.WriteLine($"Onbekende optie: {args[i]} (zie --help)");
            return 1;
    }
}

Console.WriteLine($"BoltCat.Test — rigctld op {host}:{port}{(vfoMode ? " (VFO-modus)" : "")}");
Console.WriteLine("Typ 'help' voor commando's, 'q' om te stoppen.\n");

await using var client = new RigctldClient(host, port, vfoMode);
await using var poller = new RigPoller(client);

poller.StateChanged += s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Format(s)}");
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
                Console.WriteLine(Format(poller.State));
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
return 0;

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
    return $"{f}{b}  {s.Mode,-6} {pb}  {(s.Ptt ? "TX" : "RX")}  {meter}{split}";
}
