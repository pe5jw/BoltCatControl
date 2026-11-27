using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace BoltCat.Core;

/// <summary>Instellingen om rigctld te starten.</summary>
public sealed record RigctldOptions
{
    /// <summary>Hamlib modelnummer (zie <c>rigctld -l</c>), 1 = dummy radio.</summary>
    public required int Model { get; init; }

    /// <summary>Seriële poort: COM3 (Windows) of /dev/ttyUSB0 (Linux). Leeg = geen (dummy).</summary>
    public string? SerialPort { get; init; }

    /// <summary>Baudrate, moet gelijk zijn aan de CAT instelling van de radio. Leeg = Hamlib standaard.</summary>
    public int? Baud { get; init; }

    public string ListenHost { get; init; } = "127.0.0.1";
    public int TcpPort { get; init; } = RigctldProtocol.DefaultPort;

    /// <summary>rigctld met <c>--vfo</c> starten (VFO B uitlezen). Niet voor FT-817/857/897.</summary>
    public bool VfoMode { get; init; }

    /// <summary>Extra argumenten, bijv. <c>-C stop_bits=2</c>.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Pad naar rigctld; leeg = zelf zoeken (meegeleverd, dan PATH).</summary>
    public string? Executable { get; init; }

    public IReadOnlyList<string> BuildArguments()
    {
        var a = new List<string> { "-m", Model.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(SerialPort)) { a.Add("-r"); a.Add(SerialPort.Trim()); }
        if (Baud is > 0) { a.Add("-s"); a.Add(Baud.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        a.Add("-T"); a.Add(ListenHost);
        a.Add("-t"); a.Add(TcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (VfoMode) a.Add("-o");
        a.AddRange(ExtraArgs);
        return a;
    }
}

/// <summary>Eén regel uit <c>rigctld -l</c>.</summary>
public sealed record RigModel(int Number, string Manufacturer, string Model, string Version, string Status)
{
    public override string ToString() => $"{Number,6}  {Manufacturer} {Model}  ({Status})";
}

/// <summary>
/// Start en stopt een lokale rigctld.
///
///  • Draait er al iets op de TCP poort (bijv. een rigctld die je zelf startte),
///    dan wordt die gebruikt en starten we niets (<see cref="StartedByUs"/> = false).
///  • Anders: rigctld zoeken (<see cref="FindExecutable"/>), starten, en wachten
///    tot de poort verbindingen accepteert. Stopt rigctld direct (verkeerde COM poort,
///    onbekend model), dan krijg je een exception met de laatste uitvoer van rigctld.
/// </summary>
public sealed class RigctldProcess : IAsyncDisposable
{
    private readonly Queue<string> _lastLines = new();
    private Process? _proc;

    public RigctldProcess(RigctldOptions options) => Options = options;

    public RigctldOptions Options { get; }

    /// <summary>Maximale tijd tot rigctld luistert.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>True als wij rigctld gestart hebben (en hem dus ook stoppen).</summary>
    public bool StartedByUs { get; private set; }

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>Uitvoer van rigctld (stdout + stderr) en eigen statusmeldingen.</summary>
    public event Action<string>? Output;

    /// <summary>De laatste regels uitvoer van rigctld, handig in foutmeldingen.</summary>
    public string LastOutput
    {
        get { lock (_lastLines) return string.Join(Environment.NewLine, _lastLines); }
    }

    // ── Zoeken ────────────────────────────────────────────────────────────

    public static string ExecutableName => OperatingSystem.IsWindows() ? "rigctld.exe" : "rigctld";

    /// <summary>
    /// rigctld zoeken: opgegeven pad → omgevingsvariabele BOLTCAT_RIGCTLD →
    /// meegeleverd (map <c>hamlib</c> naast de app) → PATH.
    /// </summary>
    public static string? FindExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        var env = Environment.GetEnvironmentVariable("BOLTCAT_RIGCTLD");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return Path.GetFullPath(env);

        var bundled = Path.Combine(AppContext.BaseDirectory, "hamlib", ExecutableName);
        if (File.Exists(bundled))
            return bundled;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), ExecutableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* ongeldig pad in PATH */ }
        }
        return null;
    }

    /// <summary>Accepteert er iets verbindingen op host:port?</summary>
    public static async Task<bool> IsListeningAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // ── Modellen ──────────────────────────────────────────────────────────

    /// <summary>Alle radio's die deze rigctld kent (<c>rigctld -l</c>).</summary>
    public static async Task<IReadOnlyList<RigModel>> ListModelsAsync(string executable, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-l");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Kan {executable} niet starten");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return ParseModelList(await stdout.ConfigureAwait(false));
    }

    private static readonly Regex ModelLine = new(@"^\s*(\d+)\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Columns = new(@"\t+|\s{2,}", RegexOptions.Compiled);

    /// <summary>Parse de tabel van <c>rigctld -l</c> (kolommen: Rig #, Mfg, Model, Version, Status, Macro).</summary>
    public static IReadOnlyList<RigModel> ParseModelList(string text)
    {
        var list = new List<RigModel>();
        foreach (var raw in text.Split('\n'))
        {
            var m = ModelLine.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var cols = Columns.Split(m.Groups[2].Value.Trim());
            if (cols.Length < 2) continue;
            list.Add(new RigModel(
                int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                cols[0].Trim(),
                cols[1].Trim(),
                cols.Length > 2 ? cols[2].Trim() : "",
                cols.Length > 3 ? cols[3].Trim() : ""));
        }
        return list;
    }

    /// <summary>
    /// Modellen zoeken op nummer of naam. "ft-897d", "FT897D" en "897" werken allemaal;
    /// een exacte modelnaam komt bovenaan.
    /// </summary>
    public static IReadOnlyList<RigModel> FindModels(IEnumerable<RigModel> models, string query)
    {
        query = query.Trim();
        if (int.TryParse(query, out var n))
        {
            var byNumber = models.Where(m => m.Number == n).ToList();
            if (byNumber.Count > 0) return byNumber;
        }

        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var q = Norm(query);
        if (q.Length == 0) return [];

        return models
            .Where(m => Norm(m.Model).Contains(q) || Norm(m.Manufacturer + m.Model).Contains(q))
            .OrderBy(m => Norm(m.Model) == q || Norm(m.Manufacturer + m.Model) == q ? 0 : 1)
            .ThenBy(m => m.Model.Length)
            .ThenBy(m => m.Number)
            .ToList();
    }

    /// <summary>
    /// Eén radio kiezen voor wat de gebruiker intypte (nummer of naam).
    /// Lukt dat niet eenduidig, dan is <c>Model</c> null en staan de kandidaten in <c>Candidates</c>.
    ///   "1043" → FT-897D (bestaand modelnummer)
    ///   "ft-897d" / "FT897D" → FT-897D (exacte naam)
    ///   "897" → geen nummer, dus zoeken op naam → FT-897 én FT-897D → kiezen
    /// </summary>
    public static (RigModel? Model, IReadOnlyList<RigModel> Candidates) ResolveModel(IEnumerable<RigModel> models, string query)
    {
        var all = models as IReadOnlyList<RigModel> ?? models.ToList();
        query = query.Trim();

        if (int.TryParse(query, out var n) && all.FirstOrDefault(m => m.Number == n) is { } byNumber)
            return (byNumber, new[] { byNumber });

        // Geen bestaand nummer → op naam zoeken ("897" vindt FT-897 en FT-897D)
        var found = FindModels(all, query);
        if (found.Count == 1) return (found[0], found);

        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var q2 = Norm(query);
        var exact = found.Where(m => Norm(m.Model) == q2 || Norm(m.Manufacturer + m.Model) == q2).ToList();
        if (exact.Count == 1) return (exact[0], found);

        return (null, found);
    }

    // ── Starten / stoppen ─────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        if (await IsListeningAsync(Options.ListenHost, Options.TcpPort, TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false))
        {
            StartedByUs = false;
            Log($"Er draait al een rigctld op {Options.ListenHost}:{Options.TcpPort} — die wordt gebruikt");
            return;
        }

        var exe = FindExecutable(Options.Executable)
                  ?? throw new FileNotFoundException(
                      OperatingSystem.IsWindows()
                          ? "rigctld.exe niet gevonden (verwacht in de map 'hamlib' naast de app)"
                          : "rigctld niet gevonden — installeer Hamlib: sudo apt install libhamlib-utils",
                      ExecutableName);

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in Options.BuildArguments()) psi.ArgumentList.Add(a);

        Log($"Start: {Path.GetFileName(exe)} {string.Join(' ', psi.ArgumentList)}");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) Log(d); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is { Length: > 0 } d) Log(d); };
        if (!proc.Start()) throw new InvalidOperationException($"Kan {exe} niet starten");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _proc = proc;

        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
            {
                await Task.Delay(100, ct).ConfigureAwait(false); // laatste uitvoer binnenhalen
                var code = proc.ExitCode;
                proc.Dispose();
                _proc = null;
                throw new InvalidOperationException(
                    $"rigctld stopte direct (exit code {code}). Klopt de COM poort, de baudrate en het model?" +
                    (LastOutput.Length > 0 ? Environment.NewLine + LastOutput : ""));
            }
            if (await IsListeningAsync(Options.ListenHost, Options.TcpPort, TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false))
            {
                StartedByUs = true;
                Log($"rigctld luistert op {Options.ListenHost}:{Options.TcpPort}");
                return;
            }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        await StopAsync().ConfigureAwait(false);
        throw new TimeoutException($"rigctld luistert na {StartTimeout.TotalSeconds:0} s nog niet op poort {Options.TcpPort}" +
                                   (LastOutput.Length > 0 ? Environment.NewLine + LastOutput : ""));
    }

    public async Task StopAsync()
    {
        var p = _proc;
        _proc = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                Log("rigctld gestopt");
            }
        }
        catch (InvalidOperationException) { /* al gestopt */ }
        finally
        {
            p.Dispose();
            StartedByUs = false;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Log(string line)
    {
        lock (_lastLines)
        {
            _lastLines.Enqueue(line);
            while (_lastLines.Count > 15) _lastLines.Dequeue();
        }
        Output?.Invoke(line);
    }
}
