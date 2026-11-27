using BoltCat.Core.Midi;

namespace BoltCat.Core.Tests;

public class MidiTests
{
    // ── Encoder decoderen ─────────────────────────────────────────────────

    [Theory]
    [InlineData(65, 1)] [InlineData(63, -1)] [InlineData(70, 6)] [InlineData(58, -6)] [InlineData(64, 0)]
    public void Offset_64(int value, int clicks) =>
        Assert.Equal(clicks, new EncoderSettings(EncoderMode.Offset, 64).Decode(value));

    [Theory]
    [InlineData(66, 1)] [InlineData(64, -1)] [InlineData(65, 0)]
    public void Offset_65_voor_64_66(int value, int clicks) =>
        Assert.Equal(clicks, new EncoderSettings(EncoderMode.Offset, 65).Decode(value));

    [Theory]
    [InlineData(65, 1)] [InlineData(64, -1)] [InlineData(67, 3)]
    public void Offset_tussen_64_en_65(int value, int clicks) =>
        Assert.Equal(clicks, new EncoderSettings(EncoderMode.Offset, 64.5).Decode(value));

    [Theory]
    [InlineData(1, 1)] [InlineData(3, 3)] [InlineData(127, -1)] [InlineData(125, -3)] [InlineData(0, 0)]
    public void Twos_complement_1_127(int value, int clicks) =>
        Assert.Equal(clicks, new EncoderSettings(EncoderMode.TwosComplement).Decode(value));

    [Theory]
    [InlineData(1, 1)] [InlineData(65, -1)] [InlineData(67, -3)]
    public void Signed_bit(int value, int clicks) =>
        Assert.Equal(clicks, new EncoderSettings(EncoderMode.SignedBit).Decode(value));

    [Fact]
    public void Omgedraaid() =>
        Assert.Equal(-1, new EncoderSettings(EncoderMode.Offset, 64, Invert: true).Decode(65));

    // ── Encoder herkennen ─────────────────────────────────────────────────

    [Theory]
    [InlineData(new[] { 65, 65, 65 }, new[] { 63, 63, 63 }, EncoderMode.Offset, 64.0, false)]
    [InlineData(new[] { 66, 66, 66 }, new[] { 64, 64, 64 }, EncoderMode.Offset, 65.0, false)]
    [InlineData(new[] { 65, 68, 72, 66 }, new[] { 62, 58, 63 }, EncoderMode.Offset, 64.0, false)] // snelheid
    [InlineData(new[] { 65, 65 }, new[] { 64, 64, 64 }, EncoderMode.Offset, 64.5, false)]
    [InlineData(new[] { 1, 1, 2 }, new[] { 127, 127, 126 }, EncoderMode.TwosComplement, 64.0, false)]
    [InlineData(new[] { 1, 1, 1 }, new[] { 65, 65, 66 }, EncoderMode.SignedBit, 64.0, false)]
    [InlineData(new[] { 63, 63, 63 }, new[] { 65, 65, 65 }, EncoderMode.Offset, 64.0, true)]  // andersom
    [InlineData(new[] { 127, 127 }, new[] { 1, 1, 1 }, EncoderMode.TwosComplement, 64.0, true)]
    public void Herkent_encoder(int[] right, int[] left, EncoderMode mode, double center, bool invert)
    {
        var r = EncoderLearner.Infer(right, left);
        Assert.NotNull(r.Settings);
        Assert.Equal(mode, r.Settings!.Mode);
        if (mode == EncoderMode.Offset) Assert.Equal(center, r.Settings.Center, 3);
        Assert.Equal(invert, r.Settings.Invert);

        // Rechtsom geeft altijd omhoog
        Assert.All(right, v => Assert.True(r.Settings.Decode(v) > 0, $"{v} → {r.Settings.Decode(v)}"));
        Assert.All(left, v => Assert.True(r.Settings.Decode(v) < 0, $"{v} → {r.Settings.Decode(v)}"));
    }

    [Fact]
    public void Gewone_draaiknop_wordt_niet_herkend()
    {
        var r = EncoderLearner.Infer([40, 45, 50, 55], [55, 50, 45, 40]);
        Assert.Null(r.Settings);
        Assert.Contains("0–127", r.Message);
    }

    // ── Parser ────────────────────────────────────────────────────────────

    [Fact]
    public void Parser_running_status_en_note_on_0()
    {
        var p = new MidiParser();
        // CC 16 ch1 = 65, running status: 63; Note On 60 vel 100; Note On 60 vel 0; clock ertussen
        var msgs = p.Feed([0xB0, 16, 65, 16, 63, 0xF8, 0x90, 60, 100, 60, 0]).ToList();
        Assert.Equal(4, msgs.Count);
        Assert.Equal(new MidiMessage(MidiKind.ControlChange, 1, 16, 65), msgs[0]);
        Assert.Equal(63, msgs[1].Value);
        Assert.True(msgs[2].IsPress);
        Assert.True(msgs[3].IsRelease);
    }

    [Fact]
    public void Winmm_short_message()
    {
        var m = MidiMessage.FromShort(0xB1 | (20 << 8) | (127 << 16));
        Assert.Equal(new MidiMessage(MidiKind.ControlChange, 2, 20, 127), m);
    }

    [Fact]
    public void Apparaten_opsommen_crasht_niet() => Assert.NotNull(MidiDevices.List());

    // ── MidiController met nep-controller en nep-radio ────────────────────

    private sealed class FakeMidi : IMidiInput
    {
        public string Name => "Nep controller";
        public event Action<MidiMessage>? Received;
        public void Send(MidiKind kind, int number, int value, int channel = 1) => Received?.Invoke(new(kind, channel, number, value));
        public void Dispose() { }
    }

    private static async Task<(FakeRigctld fake, RigController rig, MidiController midi, FakeMidi input)> Setup(MidiSettings settings, int step = 100)
    {
        var fake = new FakeRigctld();
        var rig = new RigController();
        await rig.ConnectAsync(new RigConnectionSettings { Model = 1, TcpPort = fake.Port });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!rig.State.Connected && DateTime.UtcNow < deadline) await Task.Delay(20);

        var midi = new MidiController(rig, () => step);
        midi.Apply(settings);
        var input = new FakeMidi();
        midi.Attach(input);
        return (fake, rig, midi, input);
    }

    /// <summary>Wachten tot de app om linksom draaien vraagt (zoals de gebruiker op het scherm ziet).</summary>
    private static Task WaitForLeftPrompt(MidiController midi)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        midi.LearnProgress += p => { if (p.Contains("LINKSOM")) tcs.TrySetResult(); };
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Vfo_encoder_stemt_af_met_stapgrootte()
    {
        var (fake, rig, midi, input) = await Setup(new MidiSettings
        {
            Vfo = new MidiControl(MidiKind.ControlChange, 1, 16),
            Encoder = new EncoderSettings(EncoderMode.Offset, 64),
        }, step: 100);
        await using var _f = fake; await using var _r = rig; using var _m = midi;

        input.Send(MidiKind.ControlChange, 16, 65);   // +1 × 100 Hz
        input.Send(MidiKind.ControlChange, 16, 67);   // +3 × 100 Hz (sneller)
        input.Send(MidiKind.ControlChange, 99, 65);   // andere knop: niets
        Assert.Equal(14_074_400, rig.DisplayFrequency);
        await Until(() => fake.FreqA == 14_074_400);

        input.Send(MidiKind.ControlChange, 16, 63);   // −1
        await Until(() => fake.FreqA == 14_074_300);
    }

    [Fact]
    public async Task Ptt_vasthouden()
    {
        var (fake, rig, midi, input) = await Setup(new MidiSettings { Ptt = new MidiControl(MidiKind.NoteOn, 1, 60), PttMode = PttMode.Hold });
        await using var _f = fake; await using var _r = rig; using var _m = midi;

        input.Send(MidiKind.NoteOn, 60, 127);
        await Until(() => fake.Ptt);
        input.Send(MidiKind.NoteOn, 60, 0);           // loslaten (note on, velocity 0)
        await Until(() => !fake.Ptt);
    }

    [Fact]
    public async Task Ptt_aan_uit_met_cc_knop()
    {
        var (fake, rig, midi, input) = await Setup(new MidiSettings { Ptt = new MidiControl(MidiKind.ControlChange, 1, 30), PttMode = PttMode.Toggle });
        await using var _f = fake; await using var _r = rig; using var _m = midi;

        input.Send(MidiKind.ControlChange, 30, 127);
        input.Send(MidiKind.ControlChange, 30, 0);
        await Until(() => fake.Ptt);
        await Until(() => rig.State.Ptt);

        input.Send(MidiKind.ControlChange, 30, 127);
        await Until(() => !fake.Ptt);
    }

    [Fact]
    public async Task Controller_met_twee_ccs_per_klik()
    {
        // Per klik: CC 64 (64/66) én CC 65 (1/127), zoals de monitor bij een echte controller liet zien
        var (fake, rig, midi, input) = await Setup(new MidiSettings(), step: 100);
        await using var _f = fake; await using var _r = rig; using var _m = midi;

        var finished = new TaskCompletionSource<MidiSettings?>();
        midi.LearnFinished += (s, _) => finished.TrySetResult(s);
        var leftPrompt = WaitForLeftPrompt(midi);
        midi.LearnVfo();
        for (int i = 0; i < 4; i++) { input.Send(MidiKind.ControlChange, 64, 66); input.Send(MidiKind.ControlChange, 65, 1); await Task.Delay(30); }
        await leftPrompt;
        for (int i = 0; i < 4; i++) { input.Send(MidiKind.ControlChange, 64, 64); input.Send(MidiKind.ControlChange, 65, 127); await Task.Delay(30); }
        var learned = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new MidiControl(MidiKind.ControlChange, 1, 64), learned!.Vfo);
        Assert.Equal(EncoderMode.Offset, learned.Encoder.Mode);
        midi.Apply(learned);
        midi.Attach(input); // in de test is er geen apparaatnaam, dus Apply sloot de nep-controller

        // Eén klik rechtsom = precies één stap (de tweede CC telt niet dubbel)
        input.Send(MidiKind.ControlChange, 64, 66);
        input.Send(MidiKind.ControlChange, 65, 1);
        Assert.Equal(14_074_100, rig.DisplayFrequency);
        await Until(() => fake.FreqA == 14_074_100);

        // PTT leren: draaien aan de VFO wordt genegeerd, de knop telt
        var ptt = new TaskCompletionSource<MidiSettings?>();
        midi.LearnFinished += (s, _) => ptt.TrySetResult(s);
        midi.LearnPtt();
        input.Send(MidiKind.ControlChange, 64, 66);
        input.Send(MidiKind.ControlChange, 65, 127);
        input.Send(MidiKind.NoteOn, 67, 127);
        var p = await ptt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new MidiControl(MidiKind.NoteOn, 1, 67), p!.Ptt);
        Assert.Equal(new MidiControl(MidiKind.ControlChange, 1, 64), p.Vfo); // VFO blijft geleerd
    }

    [Fact]
    public async Task Leren_van_vfo_en_ptt()
    {
        var (fake, rig, midi, input) = await Setup(new MidiSettings());
        await using var _f = fake; await using var _r = rig; using var _m = midi;

        var finished = new TaskCompletionSource<(MidiSettings? s, string msg)>();
        midi.LearnFinished += (s, msg) => finished.TrySetResult((s, msg));

        var leftPrompt = WaitForLeftPrompt(midi);
        midi.LearnVfo();
        foreach (var v in new[] { 66, 66, 67, 66 }) { input.Send(MidiKind.ControlChange, 21, v); await Task.Delay(30); }
        await leftPrompt;                             // knop staat stil → app vraagt linksom
        foreach (var v in new[] { 64, 64, 63, 64 }) { input.Send(MidiKind.ControlChange, 21, v); await Task.Delay(30); }

        var (learned, message) = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(learned);
        Assert.Equal(new MidiControl(MidiKind.ControlChange, 1, 21), learned!.Vfo);
        Assert.Equal(EncoderMode.Offset, learned.Encoder.Mode);
        Assert.Equal(65, learned.Encoder.Center, 3);
        Assert.Contains("CC 21", message);
        Assert.Equal(14_074_000, fake.FreqA); // tijdens leren niet afgestemd

        var ptt = new TaskCompletionSource<MidiSettings?>();
        midi.LearnFinished += (s, _) => ptt.TrySetResult(s);
        midi.LearnPtt();
        input.Send(MidiKind.NoteOn, 48, 100, channel: 2);
        var learnedPtt = await ptt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new MidiControl(MidiKind.NoteOn, 2, 48), learnedPtt!.Ptt);
        Assert.False(fake.Ptt);
    }

    // ── MIDI-knoppen ──────────────────────────────────────────────────────

    [Fact]
    public async Task Knop_leren_en_indrukken_geeft_actie()
    {
        var (fake, rig, midi, input) = await Setup(new MidiSettings
        {
            Vfo = new MidiControl(MidiKind.ControlChange, 1, 64),
            Encoder = new EncoderSettings(EncoderMode.Offset, 65),
        });
        await using var _f = fake; await using var _r = rig; using var _m = midi;

        var learned = new TaskCompletionSource<MidiSettings?>();
        midi.LearnFinished += (s, _) => learned.TrySetResult(s);
        midi.LearnButton(MidiAction.BandUp);
        input.Send(MidiKind.ControlChange, 64, 66);   // VFO draaien telt niet
        input.Send(MidiKind.NoteOn, 40, 127);
        var s = await learned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new MidiControl(MidiKind.NoteOn, 1, 40), s!.ButtonFor(MidiAction.BandUp));
        midi.Apply(s);
        midi.Attach(input);

        var actions = new System.Collections.Concurrent.ConcurrentQueue<MidiAction>();
        midi.ActionTriggered += actions.Enqueue;
        input.Send(MidiKind.NoteOn, 40, 127);  // indrukken → actie
        input.Send(MidiKind.NoteOn, 40, 0);    // loslaten → niets
        input.Send(MidiKind.NoteOn, 41, 127);  // andere knop → niets
        Assert.Equal(new[] { MidiAction.BandUp }, actions);
    }

    [Fact]
    public void Knop_doet_maar_een_ding()
    {
        var s = new MidiSettings();
        var knop = new MidiControl(MidiKind.NoteOn, 1, 36);
        s.SetPtt(knop);
        s.SetButton(MidiAction.Split, knop);       // zelfde knop → PTT vervalt
        Assert.Null(s.Ptt);
        s.SetButton(MidiAction.SwapAB, knop);      // en weg bij Split
        Assert.Null(s.ButtonFor(MidiAction.Split));
        Assert.Equal(knop, s.ButtonFor(MidiAction.SwapAB));
        s.SetPtt(knop);                            // PTT pakt hem terug
        Assert.Empty(s.Buttons);

        var copy = s.Clone();                      // kloon deelt de lijst niet
        copy.SetButton(MidiAction.BandUp, new MidiControl(MidiKind.NoteOn, 1, 37));
        Assert.Empty(s.Buttons);
    }

    [Theory]
    [InlineData(14_200_000L, 1, "17")]
    [InlineData(14_200_000L, -1, "30")]
    [InlineData(1_840_000L, -1, "160")]     // onderste band: blijft
    [InlineData(432_200_000L, 1, "70")]     // bovenste band: blijft
    [InlineData(11_000_000L, 1, "20")]      // buiten band: eerstvolgende erboven
    [InlineData(11_000_000L, -1, "30")]     // buiten band: eerstvolgende eronder
    public void Volgende_band(long hz, int dir, string expected) =>
        Assert.Equal(expected, MidiActions.NextBand(hz, dir).Name);

    [Fact]
    public void Knoppen_in_json()
    {
        var s = new MidiSettings();
        s.SetButton(MidiAction.Band20, new MidiControl(MidiKind.ControlChange, 2, 20));
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        Assert.Contains("\"Band20\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<MidiSettings>(json)!;
        Assert.Equal(new MidiControl(MidiKind.ControlChange, 2, 20), back.ButtonFor(MidiAction.Band20));
    }
}
