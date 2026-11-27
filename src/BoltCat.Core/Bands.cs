namespace BoltCat.Core;

/// <summary>Amateurband met grenzen (IARU regio 1) en een startfrequentie.</summary>
public sealed record Band(string Name, long Low, long High, long Default)
{
    public bool Contains(long hz) => hz >= Low && hz <= High;
}

public static class Bands
{
    /// <summary>160 m t/m 70 cm, IARU regio 1 grenzen.</summary>
    public static readonly IReadOnlyList<Band> All =
    [
        new("160", 1_810_000,   2_000_000,   1_840_000),
        new("80",  3_500_000,   3_800_000,   3_650_000),
        new("60",  5_351_500,   5_366_500,   5_354_000),
        new("40",  7_000_000,   7_200_000,   7_100_000),
        new("30",  10_100_000,  10_150_000,  10_120_000),
        new("20",  14_000_000,  14_350_000,  14_200_000),
        new("17",  18_068_000,  18_168_000,  18_120_000),
        new("15",  21_000_000,  21_450_000,  21_200_000),
        new("12",  24_890_000,  24_990_000,  24_940_000),
        new("10",  28_000_000,  29_700_000,  28_500_000),
        new("6",   50_000_000,  52_000_000,  50_150_000),
        new("2",   144_000_000, 146_000_000, 144_300_000),
        new("70",  430_000_000, 440_000_000, 432_200_000),
    ];

    /// <summary>Band waar deze frequentie in valt, of null (buiten de amateurbanden).</summary>
    public static Band? Find(long? hz) => hz is { } f ? All.FirstOrDefault(b => b.Contains(f)) : null;

    /// <summary>Naam voor op een knop: "160m" … "2m", "70cm".</summary>
    public static string Label(Band b) => b.Name == "70" ? "70cm" : b.Name + "m";
}

/// <summary>Vertaling tussen Hamlib modes en de mode-knoppen.</summary>
public static class RigModes
{
    public static readonly IReadOnlyList<string> Buttons = ["USB", "LSB", "CW", "AM", "FM", "DATA"];

    /// <summary>Hamlib mode → knop (CWR → CW, PKTUSB/RTTY → DATA). Null als onbekend.</summary>
    public static string? ToButton(string? hamlibMode) => hamlibMode?.ToUpperInvariant() switch
    {
        "USB" => "USB",
        "LSB" => "LSB",
        "CW" or "CWR" => "CW",
        "AM" or "SAM" or "AMS" or "AMN" => "AM",
        "FM" or "FMN" or "WFM" => "FM",
        "PKTUSB" or "PKTLSB" or "PKTFM" or "PKTAM" or "RTTY" or "RTTYR" or "USBD1" or "USBD2" or "USBD3" => "DATA",
        _ => null,
    };

    /// <summary>Knop → Hamlib mode. DATA = PKTUSB (FT8, RTTY via geluidskaart, ...).</summary>
    public static string ToHamlib(string button) => button.ToUpperInvariant() switch
    {
        "DATA" => "PKTUSB",
        var m => m,
    };

    /// <summary>Gebruikelijke zijband voor een frequentie: LSB onder 10 MHz, anders USB.</summary>
    public static string SidebandFor(long hz) => hz < 10_000_000 ? "LSB" : "USB";
}
