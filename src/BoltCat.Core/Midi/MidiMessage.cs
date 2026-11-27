using System.Text.Json.Serialization;

namespace BoltCat.Core.Midi;

[JsonConverter(typeof(JsonStringEnumConverter<MidiKind>))]
public enum MidiKind { NoteOff, NoteOn, ControlChange, Other }

/// <summary>Eén MIDI bericht. Kanaal 1–16 (zoals op de controller), nummer = noot of CC.</summary>
public readonly record struct MidiMessage(MidiKind Kind, int Channel, int Number, int Value)
{
    /// <summary>Note On met velocity 0 telt als Note Off (veel controllers doen dat zo).</summary>
    public bool IsPress => Kind switch
    {
        MidiKind.NoteOn => Value > 0,
        MidiKind.ControlChange => Value >= 64,
        _ => false,
    };

    public bool IsRelease => Kind == MidiKind.NoteOff || (Kind == MidiKind.NoteOn && Value == 0) || (Kind == MidiKind.ControlChange && Value < 64);

    /// <summary>Uit een Windows winmm "short message" (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16).</summary>
    public static MidiMessage FromShort(int msg) => FromBytes((byte)(msg & 0xFF), (byte)((msg >> 8) & 0x7F), (byte)((msg >> 16) & 0x7F));

    public static MidiMessage FromBytes(byte status, byte data1, byte data2)
    {
        int channel = (status & 0x0F) + 1;
        return (status & 0xF0) switch
        {
            0x80 => new MidiMessage(MidiKind.NoteOff, channel, data1, data2),
            0x90 => new MidiMessage(MidiKind.NoteOn, channel, data1, data2),
            0xB0 => new MidiMessage(MidiKind.ControlChange, channel, data1, data2),
            _    => new MidiMessage(MidiKind.Other, channel, data1, data2),
        };
    }

    public override string ToString() => Kind switch
    {
        MidiKind.ControlChange => $"CC {Number,3}  kanaal {Channel,2}  waarde {Value,3}",
        MidiKind.NoteOn        => $"Noot {Number,3}  kanaal {Channel,2}  aan  ({Value})",
        MidiKind.NoteOff       => $"Noot {Number,3}  kanaal {Channel,2}  uit",
        _                      => $"overig kanaal {Channel}",
    };
}

/// <summary>Een knop of draaiknop op de controller: CC of noot, op een kanaal.</summary>
public sealed record MidiControl(MidiKind Kind, int Channel, int Number)
{
    /// <summary>Noot aan/uit horen bij dezelfde knop.</summary>
    public bool Matches(MidiMessage m) =>
        m.Channel == Channel && m.Number == Number &&
        (Kind == MidiKind.ControlChange
            ? m.Kind == MidiKind.ControlChange
            : m.Kind is MidiKind.NoteOn or MidiKind.NoteOff);

    public static MidiControl From(MidiMessage m) =>
        new(m.Kind == MidiKind.ControlChange ? MidiKind.ControlChange : MidiKind.NoteOn, m.Channel, m.Number);

    public override string ToString() =>
        Kind == MidiKind.ControlChange ? $"CC {Number} · kanaal {Channel}" : $"Noot {Number} · kanaal {Channel}";
}

/// <summary>
/// Zet een ruwe byte-stroom (Linux raw MIDI) om in berichten.
/// Ondersteunt running status; SysEx en realtime berichten worden overgeslagen.
/// </summary>
public sealed class MidiParser
{
    private byte _status;
    private readonly byte[] _data = new byte[2];
    private int _count;
    private bool _inSysEx;

    public IEnumerable<MidiMessage> Feed(ReadOnlySpan<byte> bytes)
    {
        var result = new List<MidiMessage>();
        foreach (var b in bytes)
        {
            if (b >= 0xF8) continue;                     // realtime (clock, active sensing)
            if (b == 0xF0) { _inSysEx = true; continue; }
            if (b == 0xF7) { _inSysEx = false; continue; }
            if (_inSysEx) continue;

            if ((b & 0x80) != 0)                         // statusbyte
            {
                _status = b >= 0xF0 ? (byte)0 : b;       // system common: negeren
                _count = 0;
                continue;
            }
            if (_status == 0) continue;

            _data[_count++] = b;
            int needed = (_status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
            if (_count < needed) continue;

            result.Add(MidiMessage.FromBytes(_status, _data[0], needed == 2 ? _data[1] : (byte)0));
            _count = 0;                                  // running status: zelfde status blijft gelden
        }
        return result;
    }
}
