namespace BoltCat.Core.Tests;

public class RigctldClientTests
{
    private static async Task<(FakeRigctld fake, RigctldClient client)> Connect(bool vfoMode = false)
    {
        var fake = new FakeRigctld();
        var client = new RigctldClient("127.0.0.1", fake.Port, vfoMode) { Timeout = TimeSpan.FromMilliseconds(500) };
        await client.ConnectAsync();
        return (fake, client);
    }

    [Fact]
    public async Task Frequentie_lezen_en_zetten()
    {
        var (fake, client) = await Connect();
        await using var _f = fake; await using var _c = client;

        Assert.Equal(14_074_000, await client.GetFrequencyAsync());
        await client.SetFrequencyAsync(7_040_000);
        Assert.Equal(7_040_000, fake.FreqA);
        Assert.Contains(@"+\set_freq 7040000", fake.Received);
    }

    [Fact]
    public async Task Mode_lezen_en_zetten()
    {
        var (fake, client) = await Connect();
        await using var _f = fake; await using var _c = client;

        Assert.Equal(("USB", 2400), await client.GetModeAsync());
        await client.SetModeAsync("cw", 500);
        Assert.Equal("CW", fake.Mode);
        Assert.Equal(500, fake.Passband);
    }

    [Fact]
    public async Task Ptt_aan_uit()
    {
        var (fake, client) = await Connect();
        await using var _f = fake; await using var _c = client;

        await client.SetPttAsync(true);
        Assert.True(fake.Ptt);
        Assert.True(await client.GetPttAsync());
        await client.SetPttAsync(false);
        Assert.False(await client.GetPttAsync());
    }

    [Fact]
    public async Task Split_en_vfo_op()
    {
        var (fake, client) = await Connect();
        await using var _f = fake; await using var _c = client;

        await client.SetSplitAsync(true);
        Assert.Equal((true, Vfo.B), await client.GetSplitAsync());

        await client.VfoOpAsync(VfoOp.Swap);
        Assert.Equal(7_074_000, fake.FreqA);
        Assert.Equal(14_074_000, fake.FreqB);

        await client.VfoOpAsync(VfoOp.CopyAtoB);
        Assert.Equal(fake.FreqA, fake.FreqB);

        await client.VfoOpAsync(VfoOp.Toggle);
        Assert.Contains(@"+\vfo_op TOGGLE", fake.Received);
    }

    [Fact]
    public async Task VfoModus_stuurt_vfo_argument()
    {
        var (fake, client) = await Connect(vfoMode: true);
        await using var _f = fake; await using var _c = client;

        Assert.Equal(7_074_000, await client.GetFrequencyAsync(Vfo.B));
        Assert.Contains(@"+\get_freq VFOB", fake.Received);
        await client.SetPttAsync(true);
        Assert.Contains(@"+\set_ptt currVFO 1", fake.Received);
    }

    [Fact]
    public async Task S_meter()
    {
        var (fake, client) = await Connect();
        await using var _f = fake; await using var _c = client;

        Assert.Equal(-12, await client.GetStrengthAsync());
    }

    [Fact]
    public async Task Foutcode_geeft_RigctldException()
    {
        var (fake, client) = await Connect();
        await using var _f = fake; await using var _c = client;
        fake.StrengthSupported = false;

        var ex = await Assert.ThrowsAsync<RigctldException>(() => client.GetStrengthAsync());
        Assert.Equal(-11, ex.Code);
        Assert.True(client.IsConnected); // fout van de radio, verbinding blijft staan
    }

    [Fact]
    public async Task Geen_antwoord_geeft_timeout_en_verbreekt()
    {
        var (fake, client) = await Connect();
        await using var _f = fake; await using var _c = client;
        fake.Mute["get_freq"] = true;

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetFrequencyAsync());
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Geen_rigctld_geeft_fout_bij_verbinden()
    {
        int port;
        await using (var fake = new FakeRigctld()) port = fake.Port; // poort weer vrij
        await using var client = new RigctldClient("127.0.0.1", port) { Timeout = TimeSpan.FromMilliseconds(500) };

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
        Assert.False(client.IsConnected);
    }
}
