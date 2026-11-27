using System.Diagnostics;

namespace BoltCat.Core;

/// <summary>
/// Controles en installatie op Linux:
///   • rigctld (Hamlib) aanwezig? Zo niet: installeren via de pakketbeheerder
///   • toegang tot de seriële poort? Zo niet: gebruiker in de groep 'dialout'
///
/// Rechten vragen gaat in deze volgorde:
///   1. pkexec  — het normale grafische wachtwoordvenster
///   2. een terminal (x-terminal-emulator / gnome-terminal / konsole / xterm) met sudo
///   3. lukt niets: de opdracht tonen zodat je hem zelf kunt uitvoeren
/// </summary>
public static class SystemSetup
{
    /// <summary>Pakketbeheerder + pakketnaam voor Hamlib op deze distributie.</summary>
    public sealed record PackageCommand(string Manager, string Package, string[] Args)
    {
        /// <summary>Om te tonen en te kopiëren: "sudo apt-get install -y libhamlib-utils".</summary>
        public string ShellText => "sudo " + Manager + " " + string.Join(' ', Args);
    }

    public sealed record Result(bool Success, string Message, string? ManualCommand = null);

    // ── rigctld ───────────────────────────────────────────────────────────

    public static bool RigctldAvailable(string? configuredPath = null) =>
        RigctldProcess.FindExecutable(configuredPath) is not null;

    /// <summary>Hoe Hamlib hier te installeren is (null = onbekende distributie of geen Linux).</summary>
    public static PackageCommand? HamlibInstallCommand()
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (Which("apt-get") is not null) return new("apt-get", "libhamlib-utils", ["install", "-y", "libhamlib-utils"]);
        if (Which("dnf") is not null)     return new("dnf", "hamlib", ["install", "-y", "hamlib"]);
        if (Which("pacman") is not null)  return new("pacman", "hamlib", ["-S", "--noconfirm", "hamlib"]);
        if (Which("zypper") is not null)  return new("zypper", "hamlib", ["--non-interactive", "install", "hamlib"]);
        return null;
    }

    /// <summary>Hamlib installeren en daarna controleren of rigctld er is.</summary>
    public static async Task<Result> InstallHamlibAsync(Action<string>? output = null, CancellationToken ct = default)
    {
        var cmd = HamlibInstallCommand();
        if (cmd is null)
            return new(false, "Onbekende pakketbeheerder. Installeer Hamlib (rigctld) met de pakketbeheerder van je distributie.");

        var result = await RunElevatedAsync(cmd.Manager, cmd.Args, cmd.ShellText, () => RigctldAvailable(), output, ct);
        return result.Success
            ? new(true, "Hamlib is geïnstalleerd.")
            : result;
    }

    // ── Seriële poort ─────────────────────────────────────────────────────

    /// <summary>
    /// Mag deze gebruiker de poort lezen en schrijven? Null = niet te bepalen (geen Linux,
    /// poort bestaat niet). Opent de poort niet (dat kan bij sommige apparaten een reset geven).
    /// </summary>
    public static bool? SerialPortAccessible(string? port)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(port) || !File.Exists(port)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("test", ["-r", port, "-a", "-w", port])
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null) return null;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Gebruiker toevoegen aan 'dialout' (geldt pas na opnieuw inloggen).</summary>
    public static async Task<Result> FixSerialAccessAsync(Action<string>? output = null, CancellationToken ct = default)
    {
        var user = Environment.UserName;
        var group = Which("getent") is not null && GroupExists("uucp") && !GroupExists("dialout") ? "uucp" : "dialout"; // Arch: uucp
        var args = new[] { "-aG", group, user };
        var shell = $"sudo usermod -aG {group} {user}";
        var r = await RunElevatedAsync("usermod", args, shell, null, output, ct);
        return r.Success
            ? new(true, $"Je gebruiker '{user}' zit nu in de groep '{group}'. Log uit en opnieuw in (of herstart) om de poort te kunnen gebruiken.")
            : r;
    }

    private static bool GroupExists(string group)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("getent", ["group", group]) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            if (p is null) return false;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    // ── Rechten vragen ────────────────────────────────────────────────────

    /// <summary>
    /// Opdracht als root uitvoeren: pkexec, anders een terminal met sudo.
    /// <paramref name="done"/>: controle of het gelukt is (bij een terminal weten we de exitcode niet).
    /// </summary>
    private static async Task<Result> RunElevatedAsync(string program, string[] args, string shellText,
        Func<bool>? done, Action<string>? output, CancellationToken ct)
    {
        var exe = Which(program) ?? program;

        // 1. pkexec (grafisch wachtwoordvenster via polkit)
        if (Which("pkexec") is not null)
        {
            output?.Invoke("Wachtwoord vragen (pkexec)…");
            var (code, text) = await RunAsync("pkexec", [exe, .. args], output, ct);
            if (code == 0) return new(true, "Gelukt.");
            if (code is 126 or 127 && text.Contains("dismissed", StringComparison.OrdinalIgnoreCase))
                return new(false, "Geannuleerd.", shellText);
            // 127 = geen polkit agent (bijv. via xrdp): door naar de terminal
            output?.Invoke($"pkexec lukte niet (code {code}), terminal proberen…");
        }

        // 2. terminal met sudo
        var script = $"{shellText}; echo; echo 'Klaar — dit venster mag dicht.'; read -r _";
        foreach (var (term, termArgs) in Terminals(script))
        {
            if (Which(term) is null) continue;
            output?.Invoke($"Terminal openen ({term}) — typ daar je wachtwoord…");
            try
            {
                using var p = Process.Start(new ProcessStartInfo(term, termArgs) { UseShellExecute = false });
                if (p is null) continue;
            }
            catch (Exception) { continue; }

            if (done is null)
                return new(true, "Opdracht gestart in de terminal.", shellText);

            // Wachten tot het resultaat er is (terminals keren vaak direct terug)
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (done()) return new(true, "Gelukt.");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            return new(false, "Nog niet gelukt. Is de installatie in de terminal afgerond?", shellText);
        }

        // 3. zelf uitvoeren
        return new(false, "Kon geen wachtwoordvenster of terminal openen. Voer deze opdracht zelf uit in een terminal:", shellText);
    }

    private static IEnumerable<(string Term, string[] Args)> Terminals(string script)
    {
        yield return ("x-terminal-emulator", ["-e", "sh", "-c", script]);
        yield return ("gnome-terminal", ["--", "sh", "-c", script]);
        yield return ("konsole", ["-e", "sh", "-c", script]);
        yield return ("xfce4-terminal", ["-x", "sh", "-c", script]);
        yield return ("lxterminal", ["-e", "sh", "-c", script]);
        yield return ("xterm", ["-e", "sh", "-c", script]);
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, string[] args, Action<string>? output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        using var p = new Process { StartInfo = psi };
        var all = new System.Text.StringBuilder();
        void Line(string? l)
        {
            if (string.IsNullOrEmpty(l)) return;
            lock (all) all.AppendLine(l);
            output?.Invoke(l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return (127, ex.Message);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, all.ToString());
    }

    /// <summary>Programma zoeken in PATH (en de gebruikelijke sbin mappen voor usermod).</summary>
    public static string? Which(string program)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/sbin", "/sbin", "/usr/bin", "/bin"]);
        foreach (var dir in dirs.Distinct())
        {
            try
            {
                var candidate = Path.Combine(dir, program);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
