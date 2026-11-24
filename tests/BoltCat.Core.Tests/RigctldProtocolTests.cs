namespace BoltCat.Core.Tests;

public class RigctldProtocolTests
{
    [Fact]
    public void Parse_get_freq()
    {
        var r = RigctldProtocol.Parse(["get_freq:", "Frequency: 14074000", "RPRT 0"]);
        Assert.True(r.Ok);
        Assert.Equal("Frequency", r.Values[0].Key);
        Assert.Equal(14_074_000, RigctldProtocol.ParseFrequency(r.Value()));
    }

    [Fact]
    public void Parse_frequentie_met_decimalen()
    {
        Assert.Equal(7_074_000, RigctldProtocol.ParseFrequency("7074000.000000"));
    }

    [Fact]
    public void Parse_get_mode_twee_waarden()
    {
        var r = RigctldProtocol.Parse(["get_mode:", "Mode: PKTUSB", "Passband: 3000", "RPRT 0"]);
        Assert.Equal("PKTUSB", r.Value(0));
        Assert.Equal("3000", r.Value(1));
    }

    [Fact]
    public void Parse_set_heeft_geen_waarden()
    {
        var r = RigctldProtocol.Parse(["set_freq: 14074000", "RPRT 0"]);
        Assert.True(r.Ok);
        Assert.Empty(r.Values);
    }

    [Fact]
    public void Parse_foutcode()
    {
        var r = RigctldProtocol.Parse(["get_level: STRENGTH", "RPRT -11"]);
        Assert.False(r.Ok);
        Assert.Equal(-11, r.Code);
    }

    [Fact]
    public void Parse_waarde_zonder_key()
    {
        var r = RigctldProtocol.Parse(["get_level: STRENGTH", "-24", "RPRT 0"]);
        Assert.Equal(-24, RigctldProtocol.ParseInt(r.Value()));
    }

    [Theory]
    [InlineData("VFOA", Vfo.A)]
    [InlineData("VFOB", Vfo.B)]
    [InlineData("Main", Vfo.A)]
    [InlineData("currVFO", Vfo.Current)]
    public void ParseVfo(string s, Vfo expected) => Assert.Equal(expected, RigctldProtocol.ParseVfo(s));

    [Theory]
    [InlineData(-54, "S0")]
    [InlineData(-12, "S7")]
    [InlineData(0, "S9")]
    [InlineData(20, "S9+20")]
    public void SMeter_format(int db, string expected) => Assert.Equal(expected, SMeter.Format(db));

    [Fact]
    public void SMeter_fraction_schaal()
    {
        Assert.Equal(0, SMeter.Fraction(-60));
        Assert.Equal(0.6, SMeter.Fraction(0), 3);
        Assert.Equal(1, SMeter.Fraction(80));
    }
}
