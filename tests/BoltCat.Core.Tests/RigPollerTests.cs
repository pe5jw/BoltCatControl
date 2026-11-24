namespace BoltCat.Core.Tests;

public class RigPollerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static Task<RigState> WaitFor(RigPoller poller, Func<RigState, bool> condition)
    {
        var tcs = new TaskCompletionSource<RigState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RigState s) { if (condition(s)) tcs.TrySetResult(s); }
        poller.StateChanged += Handler;
        if (condition(poller.State)) tcs.TrySetResult(poller.State);
        return tcs.Task.WaitAsync(Wait).ContinueWith(t => { poller.StateChanged -= Handler; return t; }).Unwrap();
    }

    private static RigPoller NewPoller(RigctldClient client) => new(client)
    {
        Interval = TimeSpan.FromMilliseconds(30),
        ReconnectDelay = TimeSpan.FromMilliseconds(100),
    };

    [Fact]
    public async Task Leest_radio_uit()
    {
        await using var fake = new FakeRigctld();
        await using var client = new RigctldClient("127.0.0.1", fake.Port) { Timeout = TimeSpan.FromMilliseconds(500) };
        await using var poller = NewPoller(client);
        poller.Start();

        var s = await WaitFor(poller, s => s.Connected);
        Assert.Equal(14_074_000, s.FrequencyA);
        Assert.Equal("USB", s.Mode);
        Assert.Equal(-12, s.StrengthDb);
        Assert.False(s.Ptt);
        Assert.Null(s.FrequencyB); // geen VFO-modus
    }

    [Fact]
    public async Task Meldt_wijziging_van_de_radio()
    {
        await using var fake = new FakeRigctld();
        await using var client = new RigctldClient("127.0.0.1", fake.Port) { Timeout = TimeSpan.FromMilliseconds(500) };
        await using var poller = NewPoller(client);
        poller.Start();
        await WaitFor(poller, s => s.Connected);

        fake.FreqA = 3_573_000;
        fake.Ptt = true;
        var s = await WaitFor(poller, s => s.FrequencyA == 3_573_000 && s.Ptt);
        Assert.True(s.Connected);
    }

    [Fact]
    public async Task VfoModus_leest_vfo_b()
    {
        await using var fake = new FakeRigctld();
        await using var client = new RigctldClient("127.0.0.1", fake.Port, vfoMode: true) { Timeout = TimeSpan.FromMilliseconds(500) };
        await using var poller = NewPoller(client);
        poller.Start();

        var s = await WaitFor(poller, s => s.Connected && s.FrequencyB is not null);
        Assert.Equal(7_074_000, s.FrequencyB);
    }

    [Fact]
    public async Task Zonder_s_meter_ondersteuning_blijft_verbonden()
    {
        await using var fake = new FakeRigctld { StrengthSupported = false };
        await using var client = new RigctldClient("127.0.0.1", fake.Port) { Timeout = TimeSpan.FromMilliseconds(500) };
        await using var poller = NewPoller(client);
        poller.Start();

        var s = await WaitFor(poller, s => s.Connected);
        Assert.Null(s.StrengthDb);
    }

    [Fact]
    public async Task Verbindt_opnieuw_na_wegvallen()
    {
        await using var fake = new FakeRigctld();
        await using var client = new RigctldClient("127.0.0.1", fake.Port) { Timeout = TimeSpan.FromMilliseconds(300) };
        await using var poller = NewPoller(client);
        poller.Start();
        await WaitFor(poller, s => s.Connected);

        var lost = WaitFor(poller, s => !s.Connected);
        fake.DropClients();
        await lost;

        fake.FreqA = 21_074_000;
        var back = await WaitFor(poller, s => s.Connected && s.FrequencyA == 21_074_000);
        Assert.Null(back.Error);
    }

    [Fact]
    public async Task Zonder_rigctld_meldt_fout()
    {
        int port;
        await using (var tmp = new FakeRigctld()) port = tmp.Port;
        await using var client = new RigctldClient("127.0.0.1", port) { Timeout = TimeSpan.FromMilliseconds(300) };
        await using var poller = NewPoller(client);
        poller.Start();

        var s = await WaitFor(poller, s => s.Error is not null);
        Assert.False(s.Connected);
    }
}
