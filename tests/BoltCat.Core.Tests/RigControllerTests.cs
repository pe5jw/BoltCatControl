namespace BoltCat.Core.Tests;

public class RigControllerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static async Task<RigState> WaitFor(RigController c, Func<RigState, bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition(c.State))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Wachten mislukt, state: {c.State}");
            await Task.Delay(20);
        }
        return c.State;
    }

    private static RigConnectionSettings For(FakeRigctld fake) => new() { Model = 1, TcpPort = fake.Port };

    [Fact]
    public async Task Verbindt_met_bestaande_rigctld_en_leest_uit()
    {
        await using var fake = new FakeRigctld();
        await using var rig = new RigController();
        Assert.True(await rig.ConnectAsync(For(fake)));

        var s = await WaitFor(rig, s => s.Connected && s.FrequencyA is not null);
        Assert.Equal(14_074_000, s.FrequencyA);
        Assert.Equal(100, fake.CacheMs); // standaard cache 100 ms
    }

    [Fact]
    public async Task Afstemmen_laatste_wint_bij_trage_radio()
    {
        await using var fake = new FakeRigctld { SetFreqDelayMs = 100 };
        await using var rig = new RigController();
        await rig.ConnectAsync(For(fake));
        await WaitFor(rig, s => s.Connected);

        // 50 snelle stapjes, zoals draaien aan de VFO
        for (int i = 1; i <= 50; i++)
        {
            rig.TuneTo(14_074_000 + i * 10);
            Assert.Equal(14_074_000 + i * 10, rig.DisplayFrequency); // direct zichtbaar
            await Task.Delay(5);
        }

        await WaitFor(rig, s => fake.FreqA == 14_074_500);
        int sent = fake.Received.Count(l => l.Contains("set_freq"));
        Assert.True(sent < 20, $"set_freq {sent}x verstuurd — tussenstappen hadden overgeslagen moeten worden");
        await WaitFor(rig, s => s.FrequencyA == 14_074_500);
    }

    [Fact]
    public async Task Swap_valt_terug_op_toggle_bij_ft897()
    {
        await using var fake = new FakeRigctld { ToggleOnly = true };
        await using var rig = new RigController();
        var notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        rig.Notice += notices.Enqueue;
        await rig.ConnectAsync(For(fake));
        await WaitFor(rig, s => s.Connected);

        await rig.SwapVfoAsync();
        Assert.Contains(@"+\vfo_op TOGGLE", fake.Received);
        Assert.Equal(7_074_000, fake.FreqA);

        await rig.CopyAtoBAsync();
        Assert.Contains(notices, n => n.Contains("niet ondersteund"));
    }

    [Fact]
    public async Task Mode_knop_data_wordt_pktusb()
    {
        await using var fake = new FakeRigctld();
        await using var rig = new RigController();
        await rig.ConnectAsync(For(fake));
        await WaitFor(rig, s => s.Connected);

        await rig.SetModeAsync("DATA");
        Assert.Equal("PKTUSB", fake.Mode);
        var s = await WaitFor(rig, s => s.Mode == "PKTUSB");
        Assert.Equal("DATA", RigModes.ToButton(s.Mode));
    }

    [Fact]
    public async Task Verbreken_zet_ptt_uit()
    {
        await using var fake = new FakeRigctld();
        await using var rig = new RigController();
        await rig.ConnectAsync(For(fake));
        await WaitFor(rig, s => s.Connected);

        await rig.SetPttAsync(true);
        await WaitFor(rig, s => s.Ptt);
        await rig.DisconnectAsync();

        Assert.False(fake.Ptt);
        Assert.False(rig.State.Connected);
    }

    [Fact]
    public async Task Commando_zonder_verbinding_crasht_niet()
    {
        await using var rig = new RigController();
        var notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        rig.Notice += notices.Enqueue;

        await rig.SetPttAsync(true);
        rig.TuneTo(7_074_000);
        Assert.Contains(notices, n => n.Contains("niet verbonden"));
    }

    [Theory]
    [InlineData(14_200_000, "20")]
    [InlineData(7_074_000, "40")]
    [InlineData(432_200_000, "70")]
    [InlineData(11_000_000, null)]
    public void Band_vinden(long hz, string? band) => Assert.Equal(band, Bands.Find(hz)?.Name);

    [Theory]
    [InlineData("CWR", "CW")]
    [InlineData("PKTUSB", "DATA")]
    [InlineData("RTTY", "DATA")]
    [InlineData("", null)]
    public void Mode_naar_knop(string mode, string? button) => Assert.Equal(button, RigModes.ToButton(mode));
}
