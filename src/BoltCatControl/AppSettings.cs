using System.Text.Json;
using BoltCat.Core;

namespace BoltCatControl;

/// <summary>
/// Instellingen, bewaard als JSON zodat de app na een herstart met dezelfde radio opent.
///   Windows: %APPDATA%\BoltCatControl\settings.json
///   Linux  : ~/.config/BoltCatControl/settings.json
/// </summary>
public sealed class AppSettings
{
    // Radio
    /// <summary>Hamlib modelnummer, 0 = nog niet gekozen.</summary>
    public int RigModel { get; set; }
    public string RigName { get; set; } = "";
    public string SerialPort { get; set; } = "";
    public int? Baud { get; set; }

    // Geavanceerd
    public int TcpPort { get; set; } = RigctldProtocol.DefaultPort;
    public bool VfoMode { get; set; }
    public int? CacheMs { get; set; } = 100;
    public bool AdaptiveCache { get; set; } = true;
    public string? RigctldPath { get; set; }
    public bool AutoConnect { get; set; } = true;

    // Bediening
    public string Theme { get; set; } = "Amber";

    /// <summary>Schaal van de hele interface (1.0 = 100 %). Ctrl +/− /0 of Ctrl + scrollwiel.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>Laatste venstergrootte (de schaal volgt daaruit).</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int StepHz { get; set; } = 100;

    /// <summary>Laatste frequentie per band ("20" → 14 205 000).</summary>
    public Dictionary<string, long> BandMemory { get; set; } = [];

    /// <summary>Waar dit bestand vandaan kwam / naartoe gaat (niet in de JSON). Tests gebruiken een tijdelijk pad.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? StoragePath { get; set; }

    public bool IsConfigured => RigModel > 0 && (RigModel == 1 || !string.IsNullOrWhiteSpace(SerialPort));

    public RigConnectionSettings ToConnection() => new()
    {
        Model = RigModel,
        SerialPort = string.IsNullOrWhiteSpace(SerialPort) ? null : SerialPort,
        Baud = Baud,
        TcpPort = TcpPort,
        VfoMode = VfoMode,
        CacheMs = CacheMs,
        AdaptiveCache = AdaptiveCache,
        RigctldPath = RigctldPath,
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BoltCatControl", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                loaded.StoragePath = path;
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Kapotte of onleesbare instellingen: met standaardwaarden verder
        }
        return new AppSettings { StoragePath = path };
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= StoragePath ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Niet kunnen opslaan is geen reden om de app te laten vallen
        }
    }
}
