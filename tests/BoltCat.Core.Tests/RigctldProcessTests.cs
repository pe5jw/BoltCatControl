using Xunit.Abstractions;

namespace BoltCat.Core.Tests;

public class RigctldProcessTests(ITestOutputHelper output)
{
    // Ingekort voorbeeld van 'rigctld -l' (Hamlib 4.x)
    private const string ModelListSample = """
         Rig #  Mfg                    Model                   Version         Status      Macro
             1  Hamlib                 Dummy                   20240205.0      Stable      RIG_MODEL_DUMMY
             2  Hamlib                 NET rigctl              20240110.0      Stable      RIG_MODEL_NETRIGCTL
          1023  Yaesu                  FT-897                  20231109.0      Stable      RIG_MODEL_FT897
          1043  Yaesu                  FT-897D                 20231109.0      Stable      RIG_MODEL_FT897D
          1028  Yaesu                  FT-950                  20231109.0      Stable      RIG_MODEL_FT950
          3073  Icom                   IC-7300                 20231109.0      Stable      RIG_MODEL_IC7300
        """;

    [Fact]
    public void Modellijst_parsen()
    {
        var models = RigctldProcess.ParseModelList(ModelListSample);
        Assert.Equal(6, models.Count);
        var ic = Assert.Single(models, m => m.Number == 3073);
        Assert.Equal("Icom", ic.Manufacturer);
        Assert.Equal("IC-7300", ic.Model);
        Assert.Equal("Stable", ic.Status);
        Assert.Equal("NET rigctl", models[1].Model); // modelnaam met spatie
    }

    [Theory]
    [InlineData("ft-897d", 1043)]
    [InlineData("FT897D", 1043)]
    [InlineData("ft-897", 1023)]
    [InlineData("yaesu ft-950", 1028)]
    [InlineData("1028", 1028)]
    [InlineData("ic7300", 3073)]
    public void Model_zoeken_exact_bovenaan(string query, int expected)
    {
        var found = RigctldProcess.FindModels(RigctldProcess.ParseModelList(ModelListSample), query);
        Assert.Equal(expected, found[0].Number);
    }

    [Fact]
    public void Model_zoeken_op_deel_geeft_meerdere()
    {
        var found = RigctldProcess.FindModels(RigctldProcess.ParseModelList(ModelListSample), "897");
        Assert.Equal(new[] { 1023, 1043 }, found.Select(m => m.Number).Order());
    }

    [Theory]
    [InlineData("1043", 1043)]     // bestaand modelnummer
    [InlineData("ft-897d", 1043)]  // exacte naam
    [InlineData("FT897D", 1043)]
    [InlineData("950", 1028)]      // geen nummer → op naam, maar één treffer
    [InlineData("ic-7300", 3073)]
    public void Model_kiezen(string query, int expected)
    {
        var (model, _) = RigctldProcess.ResolveModel(RigctldProcess.ParseModelList(ModelListSample), query);
        Assert.Equal(expected, model?.Number);
    }

    [Fact]
    public void Model_kiezen_897_is_niet_eenduidig()
    {
        // 897 is geen Hamlib modelnummer → zoeken op naam → FT-897 en FT-897D
        var (model, candidates) = RigctldProcess.ResolveModel(RigctldProcess.ParseModelList(ModelListSample), "897");
        Assert.Null(model);
        Assert.Equal(new[] { 1023, 1043 }, candidates.Select(m => m.Number).Order());
    }

    [Fact]
    public void Model_kiezen_onbekend()
    {
        var (model, candidates) = RigctldProcess.ResolveModel(RigctldProcess.ParseModelList(ModelListSample), "ts-2000");
        Assert.Null(model);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Argumenten_opbouwen()
    {
        var args = new RigctldOptions { Model = 1043, SerialPort = "COM3", Baud = 4800 }.BuildArguments();
        Assert.Equal(new[] { "-m", "1043", "-r", "COM3", "-s", "4800", "-T", "127.0.0.1", "-t", "4532" }, args);

        var dummy = new RigctldOptions { Model = 1, TcpPort = 4600, VfoMode = true }.BuildArguments();
        Assert.Equal(new[] { "-m", "1", "-T", "127.0.0.1", "-t", "4600", "-o" }, dummy);
    }

    [Fact]
    public void Onbekend_pad_geeft_null()
    {
        Assert.Null(RigctldProcess.FindExecutable(Path.Combine(Path.GetTempPath(), "bestaat-niet", "rigctld.exe")));
    }

    [Fact]
    public async Task Bestaande_rigctld_wordt_gebruikt()
    {
        await using var fake = new FakeRigctld();
        await using var proc = new RigctldProcess(new RigctldOptions { Model = 1, TcpPort = fake.Port, Executable = "bestaat-niet" });

        await proc.StartAsync(); // niets starten: poort is al in gebruik
        Assert.False(proc.StartedByUs);
        Assert.False(proc.IsRunning);
    }

    // ── Integratie met een echte rigctld (dummy radio, model 1) ───────────
    // In CI staat rigctld klaar (Windows: Hamlib zip, Linux: libhamlib-utils).
    // Lokaal zonder rigctld wordt deze test overgeslagen.

    [Fact]
    public async Task Echte_rigctld_dummy_radio()
    {
        var exe = RigctldProcess.FindExecutable();
        if (exe is null)
        {
            output.WriteLine("rigctld niet gevonden — integratietest overgeslagen");
            Assert.Null(Environment.GetEnvironmentVariable("BOLTCAT_REQUIRE_RIGCTLD")); // in CI verplicht
            return;
        }
        output.WriteLine($"rigctld: {exe}");

        int port = FreePort();
        // -P RIG: PTT via CAT. De dummy van Hamlib 4.5.x heeft standaard "PTT type: None"
        // en weigert dan set_ptt (Ubuntu 24.04 / Pi OS Bookworm); echte radio's met CAT-PTT niet.
        await using var proc = new RigctldProcess(new RigctldOptions { Model = 1, TcpPort = port, ExtraArgs = ["-P", "RIG"] });
        proc.Output += Log;
        await proc.StartAsync();
        Assert.True(proc.StartedByUs);
        Assert.True(proc.IsRunning);

        await using var client = new RigctldClient("127.0.0.1", port) { Timeout = TimeSpan.FromSeconds(3) };
        await client.ConnectAsync();

        await client.SetFrequencyAsync(14_074_000);
        Assert.Equal(14_074_000, await client.GetFrequencyAsync());

        await client.SetModeAsync("USB", 2400);
        var (mode, pb) = await client.GetModeAsync();
        Assert.Equal("USB", mode);
        Assert.Equal(2400, pb);

        await client.SetPttAsync(true);
        Assert.True(await client.GetPttAsync());
        await client.SetPttAsync(false);
        Assert.False(await client.GetPttAsync());

        await client.SetSplitAsync(true, Vfo.B);
        Assert.True((await client.GetSplitAsync()).Split);

        // Hamlib cache (standaard vaak 1000 ms) instelbaar
        var cacheBefore = await client.GetCacheTimeoutAsync();
        output.WriteLine($"cache standaard: {cacheBefore} ms");
        await client.SetCacheTimeoutAsync(100);
        Assert.Equal(100, await client.GetCacheTimeoutAsync());

        // Onbekend commando: mag een fout geven, maar daarna moet alles weer kloppen
        client.Trace += (cmd, lines) => Log($"  >> {cmd}  <<  {string.Join(" | ", lines)}");
        try
        {
            var r = await client.SendAsync("bestaat_niet");
            Log($"onbekend commando: RPRT {r.Code}");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Log($"onbekend commando: {ex.GetType().Name}: {ex.Message}");
        }
        if (!client.IsConnected) await client.ConnectAsync();
        Assert.Equal(14_074_000, await client.GetFrequencyAsync());

        var strength = await client.GetStrengthAsync();
        output.WriteLine($"dummy S-meter: {strength} dB → {SMeter.Format(strength)}");

        // Modellijst van de echte rigctld
        var models = await RigctldProcess.ListModelsAsync(exe);
        Assert.Contains(models, m => m.Number == 1);
        Assert.NotEmpty(RigctldProcess.FindModels(models, "ft-897"));
        output.WriteLine($"{models.Count} modellen; 897: {string.Join(", ", RigctldProcess.FindModels(models, "897"))}");

        await client.DisconnectAsync();
        await proc.StopAsync();
        Assert.False(proc.IsRunning);
    }

    [Fact]
    public async Task Echte_rigctld_verkeerde_poort_geeft_duidelijke_fout()
    {
        var exe = RigctldProcess.FindExecutable();
        if (exe is null) return;

        var bad = OperatingSystem.IsWindows() ? "COM250" : "/dev/ttyBESTAATNIET";
        await using var proc = new RigctldProcess(new RigctldOptions { Model = 1043, SerialPort = bad, Baud = 4800, TcpPort = FreePort() })
        {
            StartTimeout = TimeSpan.FromSeconds(8),
        };
        proc.Output += Log;

        // Afhankelijk van de Hamlib versie stopt rigctld direct, of blijft hij draaien
        // en faalt pas het eerste commando. Beide mogen, maar nooit stil hangen.
        try
        {
            await proc.StartAsync();
            output.WriteLine("rigctld bleef draaien ondanks verkeerde poort");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            output.WriteLine($"verwachte fout: {ex.Message}");
            Assert.False(proc.IsRunning);
        }
    }

    // rigctld kan nog uitvoer geven na afloop van de test; dan niet crashen
    private void Log(string line)
    {
        try { output.WriteLine(line); } catch (InvalidOperationException) { }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
