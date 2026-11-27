using System.Text.Json.Serialization;

namespace BoltCat.Core.Midi;

/// <summary>Wat een MIDI-knop doet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MidiAction>))]
public enum MidiAction
{
    // VFO
    TuneUp, TuneDown, StepNext,
    // Mode
    ModeNext, ModeUsb, ModeLsb, ModeCw, ModeAm, ModeFm, ModeData,
    // VFO bediening
    Split, SwapAB, CopyAB,
    // Band
    BandUp, BandDown,
    Band160, Band80, Band60, Band40, Band30, Band20, Band17, Band15, Band12, Band10, Band6, Band2, Band70,
}

/// <summary>Een geleerde knop: deze MIDI knop → deze actie.</summary>
public sealed record MidiButton(MidiAction Action, MidiControl Control);

public static class MidiActions
{
    /// <summary>Groepen voor de instellingen, in volgorde.</summary>
    public static readonly IReadOnlyList<(string Group, MidiAction[] Actions)> Groups =
    [
        ("VFO",  [MidiAction.TuneUp, MidiAction.TuneDown, MidiAction.StepNext]),
        ("Mode", [MidiAction.ModeNext, MidiAction.ModeUsb, MidiAction.ModeLsb, MidiAction.ModeCw, MidiAction.ModeAm, MidiAction.ModeFm, MidiAction.ModeData]),
        ("VFO A / B", [MidiAction.Split, MidiAction.SwapAB, MidiAction.CopyAB]),
        ("Band", [MidiAction.BandUp, MidiAction.BandDown,
                  MidiAction.Band160, MidiAction.Band80, MidiAction.Band60, MidiAction.Band40, MidiAction.Band30,
                  MidiAction.Band20, MidiAction.Band17, MidiAction.Band15, MidiAction.Band12, MidiAction.Band10,
                  MidiAction.Band6, MidiAction.Band2, MidiAction.Band70]),
    ];

    public static string Label(MidiAction a) => a switch
    {
        MidiAction.TuneUp   => "VFO 1 stap omhoog",
        MidiAction.TuneDown => "VFO 1 stap omlaag",
        MidiAction.StepNext => "Stapgrootte wisselen",
        MidiAction.ModeNext => "Volgende mode",
        MidiAction.ModeUsb  => "USB",
        MidiAction.ModeLsb  => "LSB",
        MidiAction.ModeCw   => "CW",
        MidiAction.ModeAm   => "AM",
        MidiAction.ModeFm   => "FM",
        MidiAction.ModeData => "DATA",
        MidiAction.Split    => "Split aan/uit",
        MidiAction.SwapAB   => "A↔B",
        MidiAction.CopyAB   => "A→B",
        MidiAction.BandUp   => "Band omhoog",
        MidiAction.BandDown => "Band omlaag",
        _ when BandOf(a) is { } b => Bands.Label(b),
        _ => a.ToString(),
    };

    /// <summary>Bij een direct-band actie: welke band.</summary>
    public static Band? BandOf(MidiAction a)
    {
        var name = a switch
        {
            MidiAction.Band160 => "160", MidiAction.Band80 => "80", MidiAction.Band60 => "60",
            MidiAction.Band40 => "40",   MidiAction.Band30 => "30", MidiAction.Band20 => "20",
            MidiAction.Band17 => "17",   MidiAction.Band15 => "15", MidiAction.Band12 => "12",
            MidiAction.Band10 => "10",   MidiAction.Band6 => "6",   MidiAction.Band2 => "2",
            MidiAction.Band70 => "70",
            _ => null,
        };
        return name is null ? null : Bands.All.First(b => b.Name == name);
    }

    /// <summary>Bij een direct-mode actie: welke mode-knop.</summary>
    public static string? ModeOf(MidiAction a) => a switch
    {
        MidiAction.ModeUsb => "USB", MidiAction.ModeLsb => "LSB", MidiAction.ModeCw => "CW",
        MidiAction.ModeAm => "AM",   MidiAction.ModeFm => "FM",   MidiAction.ModeData => "DATA",
        _ => null,
    };

    /// <summary>Volgende/vorige band vanaf een frequentie (buiten een band: de dichtstbijzijnde in die richting).</summary>
    public static Band NextBand(long? hz, int direction)
    {
        var all = Bands.All;
        if (hz is not { } f) return direction > 0 ? all[0] : all[^1];
        int idx = all.ToList().FindIndex(b => b.Contains(f));
        if (idx >= 0)
            return all[Math.Clamp(idx + Math.Sign(direction), 0, all.Count - 1)];
        return direction > 0
            ? all.FirstOrDefault(b => b.Low > f) ?? all[^1]
            : all.LastOrDefault(b => b.High < f) ?? all[0];
    }
}
