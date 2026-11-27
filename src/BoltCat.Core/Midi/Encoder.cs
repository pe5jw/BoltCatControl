using System.Text.Json.Serialization;

namespace BoltCat.Core.Midi;

/// <summary>Hoe een draaiknop (relatieve encoder) links/rechts doorgeeft in een CC waarde.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EncoderMode>))]
public enum EncoderMode
{
    /// <summary>Rond een midden (meestal 64): 65 = +1, 63 = −1, 70 = +6 (snel draaien). Ook 64/66 (midden 65).</summary>
    Offset,
    /// <summary>1…63 = rechtsom, 127 = −1, 126 = −2, … (two's complement).</summary>
    TwosComplement,
    /// <summary>1…63 = rechtsom, 65 = −1, 66 = −2, … (signed bit).</summary>
    SignedBit,
}

/// <summary>Decodering van een encoder. Center alleen voor <see cref="EncoderMode.Offset"/>.</summary>
public sealed record EncoderSettings(EncoderMode Mode = EncoderMode.Offset, double Center = 64, bool Invert = false)
{
    /// <summary>CC waarde → aantal stappen (positief = rechtsom/omhoog, 0 = niets).</summary>
    public int Decode(int value)
    {
        int d = Mode switch
        {
            EncoderMode.TwosComplement => value == 0 ? 0 : value < 64 ? value : value - 128,
            EncoderMode.SignedBit      => value < 64 ? value : -(value - 64),
            _ => OffsetDelta(value),
        };
        return Invert ? -d : d;
    }

    private int OffsetDelta(int value)
    {
        double d = value - Center;
        if (Math.Abs(d) < 0.25) return 0;
        // Midden kan ook tussen twee waarden liggen (64/65): elke afwijking is minstens 1 stap
        return Math.Sign(d) * Math.Max(1, (int)Math.Round(Math.Abs(d), MidpointRounding.AwayFromZero));
    }

    public override string ToString()
    {
        var dir = Invert ? ", richting omgedraaid" : "";
        return Mode switch
        {
            EncoderMode.TwosComplement => $"1 / 127 (two's complement){dir}",
            EncoderMode.SignedBit      => $"1 / 65 (signed bit){dir}",
            _ => Center % 1 == 0
                ? $"rond {Center:0} ({Center - 1:0} / {Center + 1:0}){dir}"
                : $"rond {Center:0.0} ({Math.Floor(Center):0} / {Math.Ceiling(Center):0}){dir}",
        };
    }
}

/// <summary>
/// Herkent het type encoder uit wat de knop stuurt bij rechtsom en linksom draaien.
///   63/65, 64/66, 58…70 (snelheid) → Offset met midden 64 of 65
///   1 / 127                       → TwosComplement
///   1 / 65                        → SignedBit
/// Linksom en rechtsom verwisseld → Invert.
/// </summary>
public static class EncoderLearner
{
    public sealed record Result(EncoderSettings? Settings, string Message);

    public static Result Infer(IReadOnlyCollection<int> right, IReadOnlyCollection<int> left)
    {
        if (right.Count == 0 || left.Count == 0)
            return new(null, "Te weinig gegevens: draai de knop een paar klikjes rechtsom én linksom.");

        static bool NearCenter(IEnumerable<int> v) => v.All(x => Math.Abs(x - 64) <= 24);

        if (NearCenter(right) && NearCenter(left))
        {
            if (right.Min() > left.Max())
                return Ok(new EncoderSettings(EncoderMode.Offset, (right.Min() + left.Max()) / 2.0));
            if (left.Min() > right.Max())
                return Ok(new EncoderSettings(EncoderMode.Offset, (left.Min() + right.Max()) / 2.0, Invert: true));
            return new(null, $"Rechtsom ({Range(right)}) en linksom ({Range(left)}) overlappen. " +
                             "Is dit een gewone draaiknop (0–127) in plaats van een eindeloze encoder?");
        }

        static bool Low(IEnumerable<int> v) => v.All(x => x is >= 1 and <= 63);
        static bool High(IEnumerable<int> v) => v.All(x => x >= 64);

        if (Low(right) && High(left))
            return Ok(new EncoderSettings(left.Average() >= 96 ? EncoderMode.TwosComplement : EncoderMode.SignedBit));
        if (Low(left) && High(right))
            return Ok(new EncoderSettings(right.Average() >= 96 ? EncoderMode.TwosComplement : EncoderMode.SignedBit, Invert: true));

        return new(null, $"Onbekend patroon: rechtsom {Range(right)}, linksom {Range(left)}.");

        static Result Ok(EncoderSettings s) => new(s, $"Herkend: {s}");
    }

    private static string Range(IEnumerable<int> v) => v.Min() == v.Max() ? $"{v.Min()}" : $"{v.Min()}–{v.Max()}";
}
