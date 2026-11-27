namespace BoltCat.Core;

/// <summary>
/// Momentopname van de radio zoals de poller hem heeft uitgelezen.
/// Immutable: de poller maakt bij elke wijziging een nieuwe instantie,
/// zodat de GUI hem veilig op een andere thread kan gebruiken.
/// </summary>
public sealed record RigState
{
    public bool Connected { get; init; }

    /// <summary>Laatste foutmelding (verbinding of CAT), null als alles goed gaat.</summary>
    public string? Error { get; init; }

    /// <summary>Frequentie van VFO A (of de actieve VFO als rigctld niet in VFO-modus draait).</summary>
    public long? FrequencyA { get; init; }

    /// <summary>Frequentie van VFO B — alleen beschikbaar als rigctld met --vfo draait.</summary>
    public long? FrequencyB { get; init; }

    public string? Mode { get; init; }
    public int? Passband { get; init; }
    public bool Ptt { get; init; }
    public bool Split { get; init; }
    public Vfo TxVfo { get; init; } = Vfo.Current;

    /// <summary>Signaalsterkte in dB t.o.v. S9, null als de radio dit niet ondersteunt.</summary>
    public int? StrengthDb { get; init; }

    public static RigState Disconnected(string? error = null) => new() { Connected = false, Error = error };
}

/// <summary>Omrekening van rigctld STRENGTH (dB t.o.v. S9) naar S-eenheden.</summary>
public static class SMeter
{
    /// <summary>1 S-eenheid = 6 dB; S9 = 0 dB.</summary>
    public const int DbPerSUnit = 6;

    /// <summary>"S0" … "S9", daarboven "S9+10", "S9+20", ...</summary>
    public static string Format(int db)
    {
        if (db > 0) return $"S9+{db}";
        int s = 9 + (int)Math.Round(db / (double)DbPerSUnit, MidpointRounding.AwayFromZero);
        return $"S{Math.Clamp(s, 0, 9)}";
    }

    /// <summary>Positie op een meterschaal van 0..1: S0 = 0, S9 = 0.6, S9+60 = 1.</summary>
    public static double Fraction(int db)
    {
        const double s0 = -9 * DbPerSUnit;   // -54
        const double top = 60;               // S9+60
        double pos = db <= 0
            ? (db - s0) / -s0 * 0.6
            : 0.6 + db / top * 0.4;
        return Math.Clamp(pos, 0, 1);
    }
}
