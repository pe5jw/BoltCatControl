using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BoltCat.Core;

[assembly: AvaloniaTestApplication(typeof(BoltCatControl.UiTests.TestAppBuilder))]

namespace BoltCatControl.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Rendert het hoofdvenster in een paar toestanden naar PNG (CI artifact "screenshots-…"),
/// zodat de layout te bekijken is zonder de app te starten.
/// </summary>
public class ScreenshotTests
{
    private static string Dir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("BOLTCAT_SCREENSHOT_DIR") is { Length: > 0 } d
                ? d : Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // Nooit de echte settings.json van de gebruiker overschrijven
    private static readonly string TempSettings = Path.Combine(Path.GetTempPath(), $"boltcat-uitest-{Guid.NewGuid():N}.json");

    private static AppSettings Ft897() => new()
    {
        RigModel = 1043, RigName = "Yaesu FT-897D", SerialPort = "COM7", Baud = 9600, AutoConnect = false,
        StoragePath = TempSettings,
    };

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(Dir, name + ".png");
        frame!.Save(path);
        Assert.True(File.Exists(path));
    }

    private static MainWindow Open(AppSettings settings, RigState state)
    {
        var window = new MainWindow(new RigController(), settings, autoStart: false);
        window.Show();
        window.ApplyState(state);
        return window;
    }

    [AvaloniaFact]
    public void Ontvangen_20m()
    {
        var w = Open(Ft897(), new RigState
        {
            Connected = true, FrequencyA = 14_236_000, Mode = "USB", Passband = 0, StrengthDb = -12,
        });
        w.ShowNoticeForTest("Hamlib cache 1000 ms -> 100 ms");
        Capture(w, "1-ontvangen-20m");
        w.Close();
    }

    [AvaloniaFact]
    public void Zenden_split()
    {
        var w = Open(new AppSettings { RigModel = 1028, RigName = "Yaesu FT-950", SerialPort = "COM3", Baud = 38400, VfoMode = true, StoragePath = TempSettings },
            new RigState
            {
                Connected = true, FrequencyA = 7_074_000, FrequencyB = 7_076_500, Mode = "PKTUSB", Passband = 3000,
                Ptt = true, Split = true, TxVfo = Vfo.B, StrengthDb = 60,
            });
        Capture(w, "2-zenden-split");
        w.Close();
    }

    [AvaloniaFact]
    public void Radio_reageert_niet()
    {
        var w = Open(Ft897(), new RigState
        {
            Connected = true, FrequencyA = 3_650_000, Mode = "LSB", StrengthDb = 10,
            Error = "Radio reageert niet: rigctld 'get_freq': timeout naar de radio (RPRT -5)",
        });
        Capture(w, "3-radio-reageert-niet");
        w.Close();
    }

    [AvaloniaFact]
    public void Niet_verbonden()
    {
        var w = Open(Ft897(), RigState.Disconnected("rigctld stopte direct (exit code 2). Klopt de COM poort, de baudrate en het model?"));
        Capture(w, "4-niet-verbonden");
        w.Close();
    }

    [AvaloniaFact]
    public void Thema_cyaan()
    {
        BoltThemesApply("Cyan");
        var w = Open(Ft897(), new RigState { Connected = true, FrequencyA = 432_200_000, Mode = "FM", StrengthDb = -30 });
        Capture(w, "5-thema-cyaan-70cm");
        w.Close();
        BoltThemesApply("Amber");
    }

    [AvaloniaFact]
    public void Schaal_150_procent()
    {
        var settings = Ft897();
        settings.UiScale = 1.5;
        var w = Open(settings, new RigState { Connected = true, FrequencyA = 7_074_000, Mode = "PKTUSB", StrengthDb = -6 });
        Assert.True(w.Width > 1000, $"breedte {w.Width}"); // 920 × 1,5 (begrensd door het scherm)
        Capture(w, "7-schaal-150");
        w.SetScale(0.8, resizeWindow: true);
        Capture(w, "8-schaal-80");
        w.Close();
    }

    [AvaloniaFact]
    public void Klein_venster_alles_in_beeld()
    {
        var settings = Ft897();
        settings.WindowWidth = 520;   // veel smaller dan de ontwerpmaat
        settings.WindowHeight = 330;
        var w = Open(settings, new RigState { Connected = true, FrequencyA = 14_074_000, Mode = "USB", StrengthDb = -6 });
        Capture(w, "9-klein-venster-520");
        w.Close();
    }

    [AvaloniaFact]
    public void Breed_laag_venster()
    {
        var settings = Ft897();
        settings.WindowWidth = 1400;
        settings.WindowHeight = 500;
        var w = Open(settings, new RigState { Connected = true, FrequencyA = 7_074_000, Mode = "LSB", StrengthDb = -18 });
        Capture(w, "10-breed-laag");
        w.Close();
    }

    [AvaloniaFact]
    public void Instellingen()
    {
        var settings = Ft897();
        settings.Midi = new BoltCat.Core.Midi.MidiSettings
        {
            Device = "DJControl Starlight",
            Vfo = new(BoltCat.Core.Midi.MidiKind.ControlChange, 1, 16),
            Encoder = new(BoltCat.Core.Midi.EncoderMode.Offset, 65),
            Ptt = new(BoltCat.Core.Midi.MidiKind.NoteOn, 1, 48),
        };
        var w = new SettingsWindow(settings);
        w.Height = 1500; // alles in beeld voor de screenshot
        w.Show();

        // Wachten tot de radiolijst (rigctld -l) geladen is
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!w.ModelsLoaded.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
        }
        w.SearchForTest("897");
        Capture(w, "6-instellingen");
        w.Close();
    }

    private static void BoltThemesApply(string name) =>
        Theming.BoltThemes.All.First(t => t.Name == name).Apply(Application.Current!);
}
