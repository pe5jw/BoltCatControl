using System.Runtime.InteropServices;

namespace BoltCat.Core.Midi;

/// <summary>Een geopende MIDI ingang.</summary>
public interface IMidiInput : IDisposable
{
    string Name { get; }

    /// <summary>Komt binnen op een achtergrondthread (driver / leesthread).</summary>
    event Action<MidiMessage>? Received;
}

/// <summary>
/// MIDI ingangen opsommen en openen, zonder extra pakketten:
///   Windows: winmm (midiIn*)
///   Linux  : ALSA raw MIDI apparaten /dev/snd/midiC*D*
/// </summary>
public static class MidiDevices
{
    public static IReadOnlyList<string> List()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WinMidiInput.List();
            if (OperatingSystem.IsLinux()) return LinuxRawMidiInput.List();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Geen MIDI ondersteuning op dit systeem
        }
        return [];
    }

    /// <summary>Open op naam. Gooit een exception met uitleg als het niet lukt.</summary>
    public static IMidiInput Open(string name)
    {
        if (OperatingSystem.IsWindows()) return WinMidiInput.Open(name);
        if (OperatingSystem.IsLinux()) return LinuxRawMidiInput.Open(name);
        throw new PlatformNotSupportedException("MIDI wordt op dit besturingssysteem niet ondersteund");
    }
}

// ── Windows: winmm ────────────────────────────────────────────────────────

internal sealed class WinMidiInput : IMidiInput
{
    private const int CallbackFunction = 0x30000;
    private const uint MimData = 0x3C3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwSupport;
    }

    private delegate void MidiInProc(nint hMidiIn, uint wMsg, nint dwInstance, nint dwParam1, nint dwParam2);

    [DllImport("winmm.dll")] private static extern int midiInGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern int midiInGetDevCaps(nint deviceId, ref MidiInCaps caps, int size);
    [DllImport("winmm.dll")] private static extern int midiInOpen(out nint handle, nint deviceId, MidiInProc callback, nint instance, int flags);
    [DllImport("winmm.dll")] private static extern int midiInStart(nint handle);
    [DllImport("winmm.dll")] private static extern int midiInStop(nint handle);
    [DllImport("winmm.dll")] private static extern int midiInReset(nint handle);
    [DllImport("winmm.dll")] private static extern int midiInClose(nint handle);

    private readonly MidiInProc _callback; // referentie vasthouden: anders ruimt de GC hem op
    private nint _handle;

    public string Name { get; }
    public event Action<MidiMessage>? Received;

    private WinMidiInput(string name, int id)
    {
        Name = name;
        _callback = OnMidi;
        int r = midiInOpen(out _handle, id, _callback, 0, CallbackFunction);
        if (r != 0) throw new IOException($"MIDI apparaat '{name}' openen mislukt (winmm fout {r}). Wordt het al door een ander programma gebruikt?");
        midiInStart(_handle);
    }

    public static IReadOnlyList<string> List()
    {
        var names = new List<string>();
        int n = midiInGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            var caps = new MidiInCaps();
            if (midiInGetDevCaps(i, ref caps, Marshal.SizeOf<MidiInCaps>()) == 0)
                names.Add(caps.szPname);
        }
        return names;
    }

    public static IMidiInput Open(string name)
    {
        var names = List();
        int id = -1;
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) { id = i; break; }
        if (id < 0) throw new IOException($"MIDI apparaat '{name}' niet gevonden");
        return new WinMidiInput(name, id);
    }

    private void OnMidi(nint hMidiIn, uint wMsg, nint dwInstance, nint dwParam1, nint dwParam2)
    {
        if (wMsg != MimData) return;
        var msg = MidiMessage.FromShort((int)dwParam1);
        if (msg.Kind != MidiKind.Other) Received?.Invoke(msg);
    }

    public void Dispose()
    {
        var h = _handle;
        _handle = 0;
        if (h == 0) return;
        midiInStop(h);
        midiInReset(h);
        midiInClose(h);
    }
}

// ── Linux: ALSA raw MIDI (/dev/snd/midiC<kaart>D<apparaat>) ────────────────

internal sealed class LinuxRawMidiInput : IMidiInput
{
    private readonly FileStream _stream;
    private readonly Thread _thread;
    private volatile bool _stop;

    public string Name { get; }
    public event Action<MidiMessage>? Received;

    private LinuxRawMidiInput(string name, string path)
    {
        Name = name;
        try
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
        }
        catch (UnauthorizedAccessException)
        {
            throw new IOException($"Geen toegang tot {path}. Voeg je gebruiker toe aan de groep 'audio' (sudo usermod -aG audio $USER) en log opnieuw in.");
        }
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "MIDI " + name };
        _thread.Start();
    }

    private static IEnumerable<(string Name, string Path)> Devices()
    {
        if (!Directory.Exists("/dev/snd")) yield break;
        foreach (var path in Directory.GetFiles("/dev/snd", "midiC*D*").OrderBy(p => p))
        {
            var file = System.IO.Path.GetFileName(path);               // midiC1D0
            var parts = file[5..].Split('D');                           // ["1", "0"]
            var card = parts[0];
            string id = $"kaart {card}";
            try { id = File.ReadAllText($"/proc/asound/card{card}/id").Trim(); } catch { }
            yield return ($"{id} ({file})", path);
        }
    }

    public static IReadOnlyList<string> List() => Devices().Select(d => d.Name).ToList();

    public static IMidiInput Open(string name)
    {
        var dev = Devices().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (dev.Path is null) throw new IOException($"MIDI apparaat '{name}' niet gevonden");
        return new LinuxRawMidiInput(dev.Name, dev.Path);
    }

    private void ReadLoop()
    {
        var parser = new MidiParser();
        var buffer = new byte[64];
        try
        {
            while (!_stop)
            {
                int n = _stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                foreach (var m in parser.Feed(buffer.AsSpan(0, n)))
                    if (m.Kind != MidiKind.Other) Received?.Invoke(m);
            }
        }
        catch (Exception) when (_stop) { /* bij sluiten */ }
        catch (IOException) { /* apparaat losgekoppeld */ }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _stop = true;
        _stream.Dispose();
    }
}
