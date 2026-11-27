namespace BoltCat.Core.Tests;

public class SystemSetupTests
{
    [Fact]
    public void Installatie_opdracht_alleen_op_linux()
    {
        var cmd = SystemSetup.HamlibInstallCommand();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(cmd);
            return;
        }
        // CI draait op Ubuntu: apt-get met libhamlib-utils
        Assert.NotNull(cmd);
        Assert.Equal("apt-get", cmd!.Manager);
        Assert.Equal("libhamlib-utils", cmd.Package);
        Assert.Equal("sudo apt-get install -y libhamlib-utils", cmd.ShellText);
    }

    [Fact]
    public void Poorttoegang()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(SystemSetup.SerialPortAccessible("COM3"));
            return;
        }
        Assert.True(SystemSetup.SerialPortAccessible("/dev/null"));           // lezen/schrijven mag
        Assert.Null(SystemSetup.SerialPortAccessible("/dev/ttyBESTAATNIET"));  // bestaat niet: onbekend
        Assert.Null(SystemSetup.SerialPortAccessible(""));
    }

    [Fact]
    public void Rigctld_aanwezig_in_ci()
    {
        // In CI staat rigctld klaar (Windows: Hamlib zip, Linux: libhamlib-utils)
        if (Environment.GetEnvironmentVariable("BOLTCAT_REQUIRE_RIGCTLD") is null) return;
        Assert.True(SystemSetup.RigctldAvailable());
    }

    [Fact]
    public void Which_vindt_sh_op_linux()
    {
        if (OperatingSystem.IsLinux()) Assert.NotNull(SystemSetup.Which("sh"));
        Assert.Null(SystemSetup.Which("bestaat-echt-niet-12345"));
    }
}
