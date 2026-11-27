using System.Globalization;

namespace BoltCat.Core;

/// <summary>VFO selectie zoals rigctld die kent.</summary>
public enum Vfo { Current, A, B }

/// <summary>VFO operaties (Hamlib vfo_op).</summary>
public enum VfoOp
{
    /// <summary>A↔B — VFO's omwisselen (XCHG).</summary>
    Swap,
    /// <summary>A→B — VFO A naar B kopiëren (CPY).</summary>
    CopyAtoB,
    /// <summary>Actieve VFO wisselen A/B (TOGGLE) — o.a. FT-817/857/897 die geen XCHG/CPY kennen.</summary>
    Toggle,
}

/// <summary>
/// Antwoord van rigctld in "extended response" modus (commando met '+' prefix):
/// <code>
/// get_mode:          ← echo van het commando
/// Mode: USB          ← waarden, "Key: Value"
/// Passband: 2400
/// RPRT 0             ← resultaatcode (0 = OK, negatief = Hamlib fout)
/// </code>
/// </summary>
public sealed record RigctldResponse(int Code, IReadOnlyList<KeyValuePair<string, string>> Values)
{
    public bool Ok => Code == 0;

    /// <summary>Waarde op positie <paramref name="index"/> (volgorde zoals rigctld ze stuurt).</summary>
    public string? Value(int index = 0) => index < Values.Count ? Values[index].Value : null;
}

public static class RigctldProtocol
{
    public const int DefaultPort = 4532;

    /// <summary>Parse de regels van één extended response (inclusief de RPRT regel).</summary>
    public static RigctldResponse Parse(IReadOnlyList<string> lines)
    {
        int code = 0;
        var values = new List<KeyValuePair<string, string>>();

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.StartsWith("RPRT", StringComparison.Ordinal))
            {
                code = int.TryParse(line.AsSpan(4).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : -8;
                break;
            }

            // Eerste regel is de echo van het commando
            if (i == 0) continue;
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            values.Add(colon < 0
                ? new KeyValuePair<string, string>("", line.Trim())
                : new KeyValuePair<string, string>(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        return new RigctldResponse(code, values);
    }

    /// <summary>
    /// Hoort dit antwoord bij het verstuurde commando? De eerste regel is de echo
    /// ("get_freq:" of "set_freq: 14074000"). Een kaal "RPRT n" zonder echo is ook goed.
    /// </summary>
    public static bool EchoMatches(string command, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return false;
        var first = lines[0].TrimEnd('\r');
        if (lines.Count == 1 && first.StartsWith("RPRT", StringComparison.Ordinal)) return true;
        var name = command.Split(' ', 2)[0];
        return first.StartsWith(name + ":", StringComparison.Ordinal)
            || first.Equals(name, StringComparison.Ordinal);
    }

    /// <summary>VFO als rigctld argument (alleen gebruikt als rigctld met --vfo draait).</summary>
    public static string VfoName(Vfo vfo) => vfo switch
    {
        Vfo.A => "VFOA",
        Vfo.B => "VFOB",
        _     => "currVFO",
    };

    public static Vfo ParseVfo(string? s) => s?.Trim().ToUpperInvariant() switch
    {
        "VFOA" or "A" or "MAIN" => Vfo.A,
        "VFOB" or "B" or "SUB"  => Vfo.B,
        _ => Vfo.Current,
    };

    public static string VfoOpName(VfoOp op) => op switch
    {
        VfoOp.Swap     => "XCHG",
        VfoOp.CopyAtoB => "CPY",
        VfoOp.Toggle   => "TOGGLE",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>Hamlib foutcode → leesbare tekst.</summary>
    public static string ErrorText(int code) => Math.Abs(code) switch
    {
        0  => "OK",
        1  => "ongeldige parameter",
        2  => "ongeldige configuratie",
        4  => "niet geïmplementeerd",
        5  => "timeout naar de radio",
        6  => "I/O fout (seriële poort)",
        7  => "interne Hamlib fout",
        8  => "protocolfout",
        9  => "geweigerd door de radio",
        11 => "functie niet beschikbaar op deze radio",
        16 => "ongeldige VFO",
        _  => $"Hamlib fout {code}",
    };

    public static long ParseFrequency(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz)
            ? (long)Math.Round(hz)
            : throw new FormatException($"Ongeldige frequentie van rigctld: '{s}'");

    public static int ParseInt(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v)
            : throw new FormatException($"Ongeldige waarde van rigctld: '{s}'");
}

/// <summary>rigctld gaf een foutcode terug (RPRT &lt; 0).</summary>
public sealed class RigctldException(string command, int code)
    : Exception($"rigctld '{command}': {RigctldProtocol.ErrorText(code)} (RPRT {code})")
{
    public string Command { get; } = command;
    public int Code { get; } = code;
}
