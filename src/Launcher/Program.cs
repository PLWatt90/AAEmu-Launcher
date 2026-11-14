// AAEmu-Launcher - GUI launcher for an AAEmu server's ArcheAge client.
//
// Sits in the top folder of a client installation (next to Bin64\ and game_pak). On Play it
//   (a) refuses when Bin64\archeage.exe is missing,
//   (b) when the config names a ClientLocale, writes a fresh Documents\ArcheAge\system.cfg with
//       "locale = <ClientLocale>" once (marker system.cfg.lan-locale, previous file kept as
//       system.cfg.before-lan; AACFGDIR overrides the folder),
//   (c) brings game_pak up to date from the patch source (signed HTTP patch server, or a folder / share)
//       with "aapatch apply" (skipped with a warning while this folder's archeage.exe runs; given up
//       after 15 minutes),
//   (d) starts Bin64\archeage.exe with the launcher passport. With AuthMode Password its strUserToken
//       carries the account's password token (see LauncherCore.BuildToken), never the password itself.
// Server-specific values come from LauncherConfig (launcher.config.json); per-player settings live in
// launcher.json next to the exe. No password is ever written to a file or the log.
//
// Test modes (console output, no game start, no GUI):
//   --test-update <pak> <patchDir> [toolDir] [server]  run step (c) for real against the given paths
//   --test-status <pak> <patchDir> [toolDir] [server]  same with "aapatch status" (dry run)
//   --test-locale                              run step (b); point AACFGDIR at a scratch folder first
//   --print-config                             print the settings in effect and where they came from
//   --print-args <account> <server> <password> [pin] print the archeage.exe command line that Play would use
//   --print-token <account> <password>         print the password token the login server checks
//   --print-pin-token <account> <pin>          print the PIN token the login server checks
//   --test-screenshot <file.png>               render the main window into a PNG and exit
// Exit code of --test-update/--test-status: aapatch's own code (0 ok, 1 pending, 2+ error),
// 3 when the source holds no manifest.txt, 2 when no aapatch.exe could be found. For apply, 1 is an
// error (only "status" uses it for "pending") and 3 is treated like an unreachable source.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AAEmuLauncher;

internal static class Program
{
    public static LauncherConfig Config => LauncherConfig.Current;
    public static string DefaultServer => Config.DefaultServer;
    public static string[] RetiredServers => Config.RetiredServers;
    public static string ShareName => Config.ShareName;
    public static string ShareUser => Config.ShareUser;
    /// <summary>Port of the patch server (src/PatchServer) on the server machine.</summary>
    public static int PatchPort => Config.PatchPort;
    /// <summary>
    /// Login server port: the config's authPort, or AAEMU_LAUNCHER_AUTH_PORT when set (to try the launcher
    /// against a test login server on another port; players never set it).
    /// </summary>
    // Computed on use, not in a static initializer: LauncherConfig reads Program.ExeDir while loading.
    public static int AuthPort =>
        int.TryParse(Environment.GetEnvironmentVariable("AAEMU_LAUNCHER_AUTH_PORT"), out var port) && port is > 0 and < 65536 ? port : Config.AuthPort;

    /// <summary>The exe's own file name (from the assembly name), used to find launcher updates on the patch source.</summary>
    public static string LauncherExe { get; } = (typeof(Program).Assembly.GetName().Name ?? "AAEmu-Launcher") + ".exe";

    /// <summary>Window title, e.g. "ArcheAge Launcher 1.4.0".</summary>
    public static string WindowTitle => $"{Config.ProductName} {(Config.Subtitle.Length > 0 ? Config.Subtitle : "Launcher")} {VersionText}";

    /// <summary>Folder holding the launcher exe (= the client root when installed as intended).</summary>
    public static string ExeDir { get; } =
        Environment.ProcessPath is { Length: > 0 } p ? Path.GetDirectoryName(p) : AppContext.BaseDirectory;

    public static string VersionText =>
        (typeof(Program).Assembly.GetName().Version ?? new Version(0, 0)).ToString(3);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            return TestMode(args);

        InitWinForms();
        Application.Run(new MainForm());
        return 0;
    }

    private static void InitWinForms()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    // ---------------------------------------------------------------- test modes

    private static int TestMode(string[] args)
    {
        EnsureConsole();
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--test-update":
                case "--test-status":
                {
                    if (args.Length < 3)
                        return TestUsage();
                    var pak = Path.GetFullPath(args[1]);
                    var share = HttpPatchSource.IsHttp(args[2]) ? args[2] : Path.GetFullPath(args[2]);
                    var root = args.Length >= 4 ? Path.GetFullPath(args[3]) : ExeDir;
                    // optional 5th argument: the Server field (drives the default source's LAN fallbacks)
                    var server = args.Length >= 5 ? args[4] : LauncherCore.HostOfShare(share) ?? "<server-ip>";
                    var core = new LauncherCore(root, Console.WriteLine);
                    var dryRun = args[0].Equals("--test-status", StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"test: {(dryRun ? "status" : "apply")} pak={pak} share={share} server={server} toolDir={root}");
                    var outcome = core.UpdatePak(pak, share, server, dryRun);
                    Console.WriteLine($"outcome: {outcome.Kind}, exit code {outcome.ExitCode}");
                    return outcome.ExitCode;
                }
                case "--print-config":
                    PrintConfig();
                    return 0;
                case "--test-locale":
                    new LauncherCore(ExeDir, Console.WriteLine).EnsureClientLocale();
                    return 0;
                case "--print-args":
                    if (args.Length < 4)
                        return TestUsage();
                    Console.WriteLine("archeage.exe " + LauncherCore.BuildArguments(args[1], args[2], LauncherCore.BuildToken(args[1], args[3]),
                        args.Length > 4 ? LauncherCore.BuildPinToken(args[1], args[4]) : ""));
                    return 0;
                case "--print-pin-token":
                    if (args.Length < 3)
                        return TestUsage();
                    Console.WriteLine(LauncherCore.BuildPinToken(args[1], args[2]));
                    return 0;
                case "--print-token":
                    if (args.Length < 3)
                        return TestUsage();
                    Console.WriteLine(LauncherCore.BuildToken(args[1], args[2]));
                    return 0;
                case "--test-screenshot":
                    return args.Length >= 2 ? TestScreenshot(Path.GetFullPath(args[1])) : TestUsage();
                default:
                    return TestUsage();
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine("launcher test failed: " + exception);
            return 2;
        }
    }

    private static int TestUsage()
    {
        Console.WriteLine($"{LauncherExe} {VersionText} test modes:");
        Console.WriteLine("  --test-update <pak> <patchDir> [toolDir] [server]  run the game_pak update step against these paths");
        Console.WriteLine("  --test-status <pak> <patchDir> [toolDir] [server]  dry run (aapatch status)");
        Console.WriteLine("  --test-locale                              run the client-locale step (honours AACFGDIR)");
        Console.WriteLine("  --print-config                             print the settings in effect");
        Console.WriteLine("  --print-args <account> <server> <password> [pin] print the archeage.exe command line");
        Console.WriteLine("  --print-pin-token <account> <pin>          print the PIN token the login server checks");
        Console.WriteLine("  --print-token <account> <password>         print the password token the login server checks");
        Console.WriteLine("  --test-screenshot <file.png>               render the window into a PNG");
        return 64;
    }

    private static void PrintConfig()
    {
        var c = Config;
        Console.WriteLine($"{LauncherExe} {VersionText}");
        Console.WriteLine("sources: " + (c.Sources.Count == 0 ? "defaults only" : string.Join(", ", c.Sources)));
        foreach (var problem in c.Problems)
            Console.WriteLine("problem: " + problem);
        Console.WriteLine(JsonSerializer.Serialize(c, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        }));
        Console.WriteLine($"default patch source for server {c.DefaultServer}: {LauncherCore.DefaultShare(c.DefaultServer)}");
    }

    private static int TestScreenshot(string pngPath)
    {
        InitWinForms();
        using var form = new MainForm { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        form.Show();
        // Pump messages for two seconds so the background launcher-update check can finish.
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
        Console.WriteLine($"window {form.Width}x{form.Height} saved to {pngPath}");
        return 0;
    }

    // A WinExe has no console of its own. When stdout is redirected (file or pipe) Console.Out already
    // works; otherwise attach to the parent's console so test output lands in the terminal.
    private static void EnsureConsole()
    {
        var stdout = GetStdHandle(-11);
        if (stdout == IntPtr.Zero || stdout == new IntPtr(-1))
            AttachConsole(-1);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);
}

// -------------------------------------------------------------------- settings

internal sealed class Settings
{
    public const string FileName = "launcher.json";

    public string Account { get; set; } = "";
    public string Server { get; set; } = Program.DefaultServer;
    /// <summary>Empty = the default patch source (LauncherCore.DefaultShare); a folder or \\server\share path also works.</summary>
    public string Share { get; set; } = "";
    public bool UpdatePak { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Settings Load(string dir, out string problem)
    {
        problem = null;
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
            return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8), Options) ?? new Settings();
        }
        catch (Exception exception)
        {
            problem = $"{FileName} could not be read ({exception.Message}); using defaults.";
            return new Settings();
        }
    }

    /// <summary>Written to launcher.json.tmp first and swapped in, so an interrupted write never leaves a half file.</summary>
    public void Save(string dir)
    {
        var path = Path.Combine(dir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

// -------------------------------------------------------------------- the steps

internal enum UpdateKind { UpToDate, Pending, ShareUnreachable, ToolMissing, Failed }

internal readonly record struct UpdateOutcome(UpdateKind Kind, int ExitCode)
{
    /// <summary>
    /// aapatch exit codes: 0 ok; 1 means "updates pending" for status but is an error for apply;
    /// 3 (no manifest.txt) from apply is treated like an unreachable share, i.e. the game still starts.
    /// </summary>
    public static UpdateOutcome FromExitCode(int code, bool dryRun) => code switch
    {
        0 => new UpdateOutcome(UpdateKind.UpToDate, 0),
        1 when dryRun => new UpdateOutcome(UpdateKind.Pending, 1),
        3 when !dryRun => new UpdateOutcome(UpdateKind.ShareUnreachable, 3),
        _ => new UpdateOutcome(UpdateKind.Failed, code),
    };
}

/// <summary>The launch steps, free of any UI; every step reports through the log callback.</summary>
internal sealed class LauncherCore
{
    public const string PatchTool = "aapatch.exe";
    public const string ManifestName = "manifest.txt";

    private static readonly Regex AccountPattern = new("^[A-Za-z0-9]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex ServerPattern = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$", RegexOptions.CultureInvariant);

    private readonly Action<string> _log;

    public LauncherCore(string rootDir, Action<string> log)
    {
        RootDir = rootDir;
        _log = log ?? (_ => { });
    }

    /// <summary>Client root: the folder holding Bin64\ and game_pak (the launcher's own folder).</summary>
    public string RootDir { get; }
    public string Bin64Dir => Path.Combine(RootDir, Program.Config.ClientDir);
    public string ClientExe => Path.Combine(Bin64Dir, "archeage.exe");
    public string PakPath => Path.Combine(RootDir, "game_pak");

    /// <summary>Shortest password the launcher sends; the first launcher login sets the account's password.</summary>
    public const int MinPasswordLength = 4;
    public const int MinPinLength = 4;
    public const int MaxPinLength = 8;
    // Discord usernames: 2-32 lower-case letters, digits, '_' and '.', no ".." (same rule as AAEmu.Login).
    private static readonly Regex DiscordPattern = new("^[a-z0-9_.]{2,32}$", RegexOptions.CultureInvariant);

    public static bool IsValidPin(string pin) =>
        pin != null && pin.Length >= MinPinLength && pin.Length <= MaxPinLength && pin.All(char.IsAsciiDigit);

    /// <summary>A Discord username as the server stores it: trimmed, without a leading '@', lower case.</summary>
    public static string NormalizeDiscord(string name) => (name ?? "").Trim().TrimStart('@').ToLowerInvariant();

    public static bool IsValidDiscord(string normalized) =>
        DiscordPattern.IsMatch(normalized) && !normalized.Contains("..", StringComparison.Ordinal);

    public static bool IsValidAccount(string account) => account != null && AccountPattern.IsMatch(account);
    public static bool IsValidServer(string server) => server != null && ServerPattern.IsMatch(server);
    /// <summary>
    /// The default patch source: the config's patchUrl when it names one (the same for every player,
    /// whatever the server field says), otherwise the patch server on the server field's machine,
    /// http://&lt;server&gt;:&lt;patchPort&gt;/. When a patchUrl cannot be reached (e.g. a LAN whose router
    /// cannot loop the public name back), UpdatePak falls back to the server field's patch server and
    /// then to LegacyShare(server).
    /// </summary>
    public static string DefaultShare(string server) =>
        Program.Config.PatchUrl.Length > 0 ? Program.Config.PatchUrl : LanPatchUrl(server);

    /// <summary>The patch server on the server field's machine.</summary>
    public static string LanPatchUrl(string server) => $"http://{server}:{Program.PatchPort}/";

    /// <summary>The optional Windows share \\server\shareName; null when the config names no share.</summary>
    public static string LegacyShare(string server) =>
        Program.ShareName.Length > 0 && IsValidServer(server) ? $@"\\{server}\{Program.ShareName}" : null;
    public static string CmdkeyHint(string target) => $"cmdkey /add:{target} /user:{Program.ShareUser} /pass:<password>";

    /// <summary>Host part of a UNC path (\\host\share\...) or an http(s) URL, or null for a local path.</summary>
    public static string HostOfShare(string share)
    {
        if (HttpPatchSource.IsHttp(share))
            return Uri.TryCreate(share, UriKind.Absolute, out var uri) ? uri.Host : null;
        if (string.IsNullOrEmpty(share) || !(share.StartsWith(@"\\") || share.StartsWith("//")))
            return null;
        var rest = share[2..];
        var cut = rest.IndexOfAny(['\\', '/']);
        var host = cut < 0 ? rest : rest[..cut];
        return host.Length == 0 ? null : host;
    }

    // ---- (a) client present --------------------------------------------------------------

    public bool ClientPresent()
    {
        if (File.Exists(ClientExe))
            return true;
        _log($"archeage.exe not found in \"{Bin64Dir}\". Put this launcher in the client folder, next to Bin64 and game_pak.");
        return false;
    }

    // ---- (b) client locale ---------------------------------------------------------------

    public static string ConfigDir()
    {
        var dir = Environment.GetEnvironmentVariable("AACFGDIR");
        if (!string.IsNullOrWhiteSpace(dir))
            return dir;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ArcheAge");
    }

    /// <summary>
    /// Once per PC, when the config names a clientLocale: set any existing system.cfg aside
    /// (system.cfg.before-lan) and write a fresh one holding only "locale = &lt;clientLocale&gt;"; the marker
    /// system.cfg.lan-locale stops this from ever running again.
    /// </summary>
    public void EnsureClientLocale()
    {
        var locale = Program.Config.ClientLocale;
        if (locale.Length == 0)
            return;
        var dir = ConfigDir();
        Directory.CreateDirectory(dir);
        var cfg = Path.Combine(dir, "system.cfg");
        var marker = Path.Combine(dir, "system.cfg.lan-locale");
        if (File.Exists(marker))
        {
            _log($"Client locale: already prepared ({marker} present), settings file left alone.");
            return;
        }
        if (File.Exists(cfg))
        {
            var backup = cfg + ".before-lan";
            if (!File.Exists(backup))
                File.Copy(cfg, backup, true);
            File.Delete(cfg);
        }
        var ascii = new UTF8Encoding(false);
        File.WriteAllText(cfg, $"locale = {locale}\r\n", ascii);
        File.WriteAllText(marker, locale + "\r\n", ascii);
        _log($"Wrote a fresh \"{cfg}\" with locale = {locale} (previous file kept as system.cfg.before-lan).");
    }

    // ---- (c) game_pak update ------------------------------------------------------------

    public UpdateOutcome UpdatePak(string share, string server, bool dryRun) => UpdatePak(PakPath, share, server, dryRun);

    /// <summary>
    /// Folder or share source: when it holds manifest.txt, copy aapatch.exe from it if missing or newer
    /// and run "aapatch apply|status pak share" with its output streamed into the log.
    /// </summary>
    public UpdateOutcome UpdatePak(string pakPath, string share, string server, bool dryRun)
    {
        if (HttpPatchSource.IsHttp(share))
        {
            var httpOutcome = UpdatePakHttp(pakPath, share, dryRun);
            // The default (public) source is unreachable from a LAN whose router does not loop the public
            // name back, and while the server PC runs no patch server: try the patch server under the
            // server field's address, then the LAN share. A refused signature never falls back.
            if (httpOutcome.Kind != UpdateKind.ShareUnreachable || !string.Equals(share, DefaultShare(server), StringComparison.OrdinalIgnoreCase))
                return httpOutcome;
            var lanUrl = LanPatchUrl(server);
            if (IsValidServer(server) && !string.Equals(lanUrl, share, StringComparison.OrdinalIgnoreCase))
            {
                _log($"Trying the patch server on the server address {lanUrl} instead ...");
                httpOutcome = UpdatePakHttp(pakPath, lanUrl, dryRun);
                if (httpOutcome.Kind != UpdateKind.ShareUnreachable)
                    return httpOutcome;
            }
            var legacy = LegacyShare(server);
            if (legacy == null)
                return httpOutcome;
            _log($"Trying the share {legacy} instead ...");
            return UpdatePak(pakPath, legacy, server, dryRun);
        }
        _log($"Checking patch share {share} ...");
        bool reachable;
        try
        {
            reachable = File.Exists(Path.Combine(share, ManifestName));
        }
        catch (Exception exception)
        {
            _log($"Patch share check failed: {exception.Message}");
            reachable = false;
        }
        if (!reachable)
        {
            string detail;
            try { detail = Directory.Exists(share) ? " (the folder answers but holds no manifest.txt)" : ""; }
            catch { detail = ""; }
            _log($"Patch share {share} not reachable{detail}; {(dryRun ? "cannot check game_pak" : "starting without checking game_pak")}.");
            if (HostOfShare(share) is { } shareHost)
            {
                _log("If Windows refused the share, store the credential once with \"Store share credential...\" or run:");
                _log("  " + CmdkeyHint(shareHost));
            }
            return new UpdateOutcome(UpdateKind.ShareUnreachable, 3);
        }

        var remoteTool = Path.Combine(share, PatchTool);
        var localTool = Path.Combine(RootDir, PatchTool);
        try
        {
            var remote = new FileInfo(remoteTool);
            if (remote.Exists)
            {
                // Copy when the local file is missing or differs in size or time (either direction, so a
                // rollback on the share is picked up too; 2 s tolerance: FAT/exFAT and some SMB servers
                // round timestamps). The copy lands in aapatch.exe.download first and is swapped in whole,
                // so an interrupted copy can never leave a truncated aapatch.exe with a fresh timestamp.
                var local = new FileInfo(localTool);
                var differs = !local.Exists || local.Length != remote.Length
                    || (remote.LastWriteTimeUtc - local.LastWriteTimeUtc).Duration() > TimeSpan.FromSeconds(2);
                if (differs)
                {
                    var download = localTool + ".download";
                    if (File.Exists(download))
                        File.Delete(download);
                    File.Copy(remoteTool, download, true);
                    File.Move(download, localTool, overwrite: true);
                    _log($"Copied {PatchTool} from the share ({new FileInfo(localTool).Length:N0} bytes, {remote.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}).");
                }
            }
        }
        catch (Exception exception)
        {
            _log($"Could not copy {PatchTool} from the share: {exception.Message}");
        }
        if (!File.Exists(localTool))
        {
            _log($"{PatchTool} not found next to the launcher; skipping the game_pak update.");
            return new UpdateOutcome(UpdateKind.ToolMissing, 2);
        }
        if (ProcessRunningFrom("aapatch", localTool))
        {
            _log($"{PatchTool} is still running from an earlier start; not starting another one.");
            return new UpdateOutcome(UpdateKind.Failed, 2);
        }

        _log($"{(dryRun ? "Checking" : "Updating")} {pakPath} against {share} ...");
        var exitCode = RunPatchTool(localTool, dryRun ? "status" : "apply", pakPath, share, null);
        if (exitCode == null)
            return new UpdateOutcome(UpdateKind.Failed, 2);
        var outcome = UpdateOutcome.FromExitCode(exitCode.Value, dryRun);
        if (outcome.Kind == UpdateKind.ShareUnreachable)
            _log($"{PatchTool} found no {ManifestName} on the share; starting without checking game_pak.");
        return outcome;
    }

    /// <summary>
    /// Runs "aapatch verb pak dir" with its output streamed into the log (and to onLine when given).
    /// Returns the exit code, or null when it could not run or did not finish in time.
    /// </summary>
    private int? RunPatchTool(string tool, string verb, string pakPath, string patchDir, Action<string> onLine)
    {
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = RootDir,
        };
        info.ArgumentList.Add(verb);
        info.ArgumentList.Add(pakPath);
        info.ArgumentList.Add(patchDir);

        int exitCode;
        try
        {
            using var process = new Process { StartInfo = info };
            DataReceivedEventHandler handler = (_, e) =>
            {
                if (e.Data == null)
                    return;
                _log("  " + e.Data);
                onLine?.Invoke(e.Data);
            };
            process.OutputDataReceived += handler;
            process.ErrorDataReceived += handler;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(PatchTimeout))
            {
                // Left running on purpose: killing it half-way through a pak write would be worse.
                _log($"{PatchTool} has not finished after {PatchTimeout.TotalMinutes:0} minutes; giving up on this start (it is left running).");
                return null;
            }
            process.WaitForExit(); // the timed overload returns before the redirected output is drained
            exitCode = process.ExitCode;
        }
        catch (Exception exception)
        {
            _log($"Could not run {PatchTool}: {exception.Message}");
            return null;
        }
        _log($"{PatchTool} {verb} finished with exit code {exitCode}.");
        return exitCode;
    }

    /// <summary>Folder in the client root that holds what the HTTP update downloads until aapatch has applied it.</summary>
    public string DownloadDir => Path.Combine(RootDir, "patch-download");

    /// <summary>
    /// The game_pak update against the patch server: fetch index.txt and manifest.txt, refresh aapatch.exe,
    /// let "aapatch status" name the entries this pak lacks, download only those into patch-download,
    /// then "aapatch apply" against that folder. An unreachable server is treated like an unreachable share.
    /// </summary>
    private UpdateOutcome UpdatePakHttp(string pakPath, string url, bool dryRun)
    {
        _log($"Checking patch server {url} ...");
        var source = new HttpPatchSource(url, _log);
        byte[] manifestBytes;
        Dictionary<string, IndexEntry> index;
        try
        {
            manifestBytes = source.GetBytes(ManifestName);
            index = manifestBytes == null ? null : source.GetSignedIndex();
        }
        catch (PatchTrustException exception)
        {
            // Not an unreachable server: something answered with data the signing key does not vouch for.
            // Nothing from it is used and the default source does not fall back to the share either.
            return Refused(url, exception.Message);
        }
        catch (Exception exception)
        {
            _log($"Patch server {url} not reachable ({exception.GetBaseException().Message}); {(dryRun ? "cannot check game_pak" : "starting without checking game_pak")}.");
            _log($"The server PC must run the patch server (Start-PatchServer.bat), let TCP port {new Uri(url).Port} through its firewall " +
                 "and, for a public address, have the router forward that port to it.");
            return new UpdateOutcome(UpdateKind.ShareUnreachable, 3);
        }
        if (manifestBytes == null || index == null)
        {
            _log($"Patch server {url} answers but has no {(manifestBytes == null ? ManifestName : HttpPatchSource.IndexName)}; " +
                 $"{(dryRun ? "cannot check game_pak" : "starting without checking game_pak")}. Run Make-Patch.ps1 on the server PC.");
            return new UpdateOutcome(UpdateKind.ShareUnreachable, 3);
        }
        if (!index.TryGetValue(ManifestName, out var manifestEntry) || !HttpPatchSource.BytesMatch(manifestBytes, manifestEntry))
        {
            _log($"The patch server's {ManifestName} does not match its signed index; it is probably publishing a new patch right now. Click Play again in a minute.");
            return new UpdateOutcome(UpdateKind.Failed, 2);
        }

        List<(string Name, long Size, string Md5)> entries;
        string id;
        try
        {
            (id, entries) = HttpPatchSource.ParseManifest(Encoding.UTF8.GetString(manifestBytes));
        }
        catch (PatchTrustException exception)
        {
            return Refused(url, exception.Message);
        }
        catch (Exception exception)
        {
            _log($"Patch server sent an unreadable {ManifestName}: {exception.Message}");
            return new UpdateOutcome(UpdateKind.Failed, 2);
        }
        foreach (var entry in entries)
        {
            if (!index.TryGetValue(entry.Name, out var listed) || listed.Size != entry.Size)
            {
                _log($"The patch server is publishing a new patch right now ({entry.Name} not yet in step); click Play again in a minute.");
                return new UpdateOutcome(UpdateKind.Failed, 2);
            }
        }
        _log($"Patch server offers patch {id} ({entries.Count} entries, signature verified).");

        // aapatch.exe itself
        var localTool = Path.Combine(RootDir, PatchTool);
        if (index.TryGetValue(PatchTool, out var toolEntry))
        {
            try
            {
                if (!HttpPatchSource.FileMatches(localTool, toolEntry.Size, toolEntry.Sha256))
                {
                    source.Download(toolEntry, localTool);
                    _log($"Downloaded {PatchTool} ({toolEntry.Size:N0} bytes).");
                }
            }
            catch (Exception exception)
            {
                _log($"Could not download {PatchTool}: {exception.Message}");
            }
        }
        if (!File.Exists(localTool))
        {
            _log($"{PatchTool} not found next to the launcher; skipping the game_pak update.");
            return new UpdateOutcome(UpdateKind.ToolMissing, 2);
        }
        if (ProcessRunningFrom("aapatch", localTool))
        {
            _log($"{PatchTool} is still running from an earlier start; not starting another one.");
            return new UpdateOutcome(UpdateKind.Failed, 2);
        }

        // Loose client files (index paths "files/<path under the client root>"), e.g. the 1.2 client's
        // game\db\compact.sqlite3, which the client reads outside game_pak. Same signed index, same checks.
        int loosePending;
        try
        {
            loosePending = SyncLooseFiles(source, index, dryRun);
        }
        catch (PatchTrustException exception)
        {
            return Refused(url, exception.Message);
        }
        catch (Exception exception)
        {
            _log($"Updating the client files failed: {exception.Message}");
            return new UpdateOutcome(UpdateKind.Failed, 2);
        }

        // Which entries does this pak lack? "aapatch status" reads only manifest.txt for that.
        var staging = DownloadDir;
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, ManifestName), manifestBytes);
        }
        catch (Exception exception)
        {
            _log($"Could not prepare {staging}: {exception.Message}");
            return new UpdateOutcome(UpdateKind.Failed, 2);
        }
        var statusLines = new List<string>();
        _log($"Comparing {pakPath} with patch {id} ...");
        var status = RunPatchTool(localTool, "status", pakPath, staging, statusLines.Add);
        if (status == null)
            return new UpdateOutcome(UpdateKind.Failed, 2);
        if (status == 0 && (dryRun || statusLines.Any(l => l.StartsWith("up to date", StringComparison.Ordinal))))
        {
            TryDeleteDownloads(staging);
            return dryRun && loosePending > 0 ? new UpdateOutcome(UpdateKind.Pending, 1) : new UpdateOutcome(UpdateKind.UpToDate, 0);
        }
        if (status is not (0 or 1))
            return new UpdateOutcome(UpdateKind.Failed, status.Value);

        var pending = entries
            .Where(e => statusLines.Any(l => l.TrimStart().StartsWith("update ", StringComparison.Ordinal)
                                             && l.Contains(" " + e.Name + " (", StringComparison.Ordinal)))
            .Select(e => index[e.Name])
            .ToList();
        var wire = pending.Sum(HttpPatchSource.WireSize);
        if (dryRun)
        {
            _log($"{pending.Count} entries to update, {wire / 1048576.0:N1} MB to download.");
            return new UpdateOutcome(UpdateKind.Pending, 1);
        }

        if (pending.Count > 0)
            _log($"Downloading {pending.Count} entries ({wire / 1048576.0:N1} MB) into {staging} ...");
        foreach (var entry in pending)
        {
            try
            {
                source.Download(entry, HttpPatchSource.LocalPath(staging, entry.Path));
            }
            catch (PatchTrustException exception)
            {
                return Refused(url, exception.Message);
            }
            catch (Exception exception)
            {
                _log($"Download of {entry.Path} failed: {exception.Message}");
                return new UpdateOutcome(UpdateKind.Failed, 2);
            }
        }
        if (pending.Count > 0)
            _log($"Downloaded {pending.Count} entries.");

        _log($"Updating {pakPath} ...");
        var apply = RunPatchTool(localTool, "apply", pakPath, staging, null);
        if (apply == null)
            return new UpdateOutcome(UpdateKind.Failed, 2);
        var outcome = UpdateOutcome.FromExitCode(apply.Value, dryRun: false);
        if (outcome.Kind == UpdateKind.UpToDate)
            TryDeleteDownloads(staging);
        return outcome;
    }

    /// <summary>Prefix of index paths that are loose files under the client root, not game_pak entries.</summary>
    public const string LoosePrefix = "files/";

    /// <summary>
    /// Brings every "files/..." entry of the signed index into the client root (download, size + SHA-256
    /// check, swap in whole). Returns how many differed; in a dry run nothing is written.
    /// </summary>
    private int SyncLooseFiles(HttpPatchSource source, Dictionary<string, IndexEntry> index, bool dryRun)
    {
        var loose = index.Values.Where(e => e.Path.StartsWith(LoosePrefix, StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        if (loose.Count == 0)
            return 0;
        var pending = new List<(IndexEntry Entry, string Target)>();
        foreach (var entry in loose)
        {
            var target = HttpPatchSource.LocalPath(RootDir, entry.Path[LoosePrefix.Length..]);
            if (!HttpPatchSource.FileMatches(target, entry.Size, entry.Sha256))
                pending.Add((entry, target));
        }
        if (pending.Count == 0)
        {
            _log($"Client files: all {loose.Count} up to date.");
            return 0;
        }
        var wire = pending.Sum(p => HttpPatchSource.WireSize(p.Entry));
        if (dryRun)
        {
            _log($"Client files: {pending.Count} of {loose.Count} to update ({wire / 1048576.0:N1} MB).");
            foreach (var p in pending)
                _log("    update   " + p.Entry.Path[LoosePrefix.Length..]);
            return pending.Count;
        }
        _log($"Client files: downloading {pending.Count} of {loose.Count} ({wire / 1048576.0:N1} MB) ...");
        foreach (var p in pending)
        {
            source.Download(p.Entry, p.Target);
            _log("    updated  " + p.Entry.Path[LoosePrefix.Length..]);
        }
        return pending.Count;
    }

    private UpdateOutcome Refused(string url, string reason)
    {
        _log($"REFUSED: {reason}. Nothing from {url} is used and the game is not started.");
        _log("If the server PC is publishing a patch right now, click Play again in a minute. If this keeps happening, " +
             "the download is being tampered with on the way, or this launcher is older than the server's signing key: " +
             $"ask the server owner for a fresh {Program.LauncherExe}.");
        return new UpdateOutcome(UpdateKind.Failed, 2);
    }

    private void TryDeleteDownloads(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        catch (Exception exception)
        {
            _log($"Could not remove {staging}: {exception.Message} (harmless)");
        }
    }

    /// <summary>How long one aapatch run may take before the launcher stops waiting for it.</summary>
    public static readonly TimeSpan PatchTimeout = TimeSpan.FromMinutes(15);

    // ---- (d) game already running -------------------------------------------------------

    /// <summary>True while this folder's Bin64\archeage.exe runs (a client from another folder does not count).</summary>
    public bool GameRunning() => ProcessRunningFrom("archeage", ClientExe);

    /// <summary>
    /// True when a process of that name runs from the given exe (path compared case-insensitively).
    /// A process whose path cannot be read (access denied, e.g. an elevated instance) counts as running.
    /// </summary>
    private static bool ProcessRunningFrom(string processName, string exePath)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch { return false; }
        try
        {
            var wanted = Path.GetFullPath(exePath);
            foreach (var process in processes)
            {
                string path;
                try { path = process.MainModule?.FileName; }
                catch (InvalidOperationException) { continue; } // exited in the meantime
                catch { return true; }
                if (path != null && string.Equals(Path.GetFullPath(path), wanted, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    // ---- (e) start the client ----------------------------------------------------------

    /// <summary>
    /// The password token sent as strUserToken: "v1:" + hex SHA-256 of "aaemu-launcher|&lt;account in lower
    /// case&gt;|&lt;password&gt;". The login server (AAEmu.Login LauncherPasswordRules) checks exactly this, so the
    /// two formulas must stay identical. Only hex characters travel, so nothing in it needs escaping.
    /// </summary>
    public static string BuildToken(string account, string password) =>
        "v1:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("aaemu-launcher|" + account.ToLowerInvariant() + "|" + password)));

    /// <summary>
    /// The PIN token: "p1:" + hex SHA-256 of "aaemu-launcher-pin|&lt;account lower&gt;|&lt;pin&gt;"; must match the
    /// server's LauncherPasswordRules.ComputePinToken.
    /// </summary>
    public static string BuildPinToken(string account, string pin) =>
        "p1:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("aaemu-launcher-pin|" + account.ToLowerInvariant() + "|" + pin)));

    /// <summary>
    /// The launcher passport the client takes with -k, carrying the account's password token and, for
    /// accounts with a PIN, the PIN token (strPinToken; empty for accounts without one).
    /// </summary>
    public static string BuildPassport(string account, string server, string token, string pinToken = "") =>
        $$"""{"source":"launcher","strUserToken":"{{token}}","strPinToken":"{{pinToken}}","StrUserName":"{{account}}","serverId":"1","selectedServerId":"1","channelId":"1","subchannelId":"1","gameId":"1","sIp":"{{server}}","sIps":"{{server}}","sPort":"{{Program.AuthPort}}","PASSPORTSVRIP":"{{server}}","extension":""}""";

    /// <summary>The archeage.exe argument string (quotes inside the passport escaped with a backslash).</summary>
    public static string BuildArguments(string account, string server, string token, string pinToken = "")
    {
        var passport = BuildPassport(account, server, token, pinToken).Replace("\"", "\\\"");
        var template = Program.Config.ClientArguments;
        if (template.Length > 0)
            return template
                .Replace("{account}", account).Replace("{server}", server)
                .Replace("{port}", Program.AuthPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{token}", token).Replace("{pinToken}", pinToken).Replace("{passport}", passport)
                .Replace("{root}", RootDirForArgs).Replace("{clientExe}", ClientExeForArgs);
        return $"-k \"{passport}\" +auth_serveraddr {server} +auth_serverport {Program.AuthPort} +auth_ip {server} +auth_port {Program.AuthPort}";
    }

    // Paths for the {root} / {clientExe} placeholders (set by StartGame; the client root of this launcher).
    private static string RootDirForArgs = Program.ExeDir.TrimEnd(Path.DirectorySeparatorChar);
    private static string ClientExeForArgs = Path.Combine(RootDirForArgs, Program.Config.ClientDir, "archeage.exe");

    public void StartGame(string account, string server, string password, string pin)
    {
        _log($"Starting ArcheAge as account \"{account}\" against {server}:{Program.AuthPort} ...");
        RootDirForArgs = RootDir.TrimEnd(Path.DirectorySeparatorChar);
        ClientExeForArgs = ClientExe;
        var startExe = Program.Config.StartExe.Length > 0 ? Path.Combine(RootDir, Program.Config.StartExe) : ClientExe;
        if (!File.Exists(startExe))
            throw new FileNotFoundException($"{startExe} not found");
        var info = new ProcessStartInfo
        {
            FileName = startExe,
            Arguments = BuildArguments(account, server, BuildToken(account, password),
                pin.Length > 0 ? BuildPinToken(account, pin) : ""), // verbatim
            WorkingDirectory = Program.Config.StartExe.Length > 0 ? Path.GetDirectoryName(startExe)! : Bin64Dir,
            UseShellExecute = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows did not start the process.");
        _log($"archeage.exe started (process id {process.Id}).");
    }

    // ---- share credential ---------------------------------------------------------------

    /// <summary>Runs cmdkey /add:target /user:aapatch /pass:... ; the password is never logged.</summary>
    public bool StoreShareCredential(string target, string password)
    {
        _log($"Running cmdkey /add:{target} /user:{Program.ShareUser} /pass:******** ...");
        // Full path: with UseShellExecute = false a bare name is looked up in the application folder first.
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmdkey.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("/add:" + target);
        info.ArgumentList.Add("/user:" + Program.ShareUser);
        info.ArgumentList.Add("/pass:" + password);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("cmdkey did not start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        foreach (var line in output.Split('\n'))
            if (line.Trim().Length > 0)
                _log("  " + line.Trim());
        _log(process.ExitCode == 0
            ? $"Credential for {target} stored in the Windows credential store."
            : $"cmdkey failed with exit code {process.ExitCode}.");
        return process.ExitCode == 0;
    }

    // ---- launcher self-update -----------------------------------------------------------

    /// <summary>
    /// When the share holds a newer launcher exe, copies it next to this one as "...exe.new" and returns
    /// that path; null when there is nothing to do. Never throws.
    /// </summary>
    public string StageLauncherUpdate(string share, string server = "")
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
            return null;
        var staged = self + ".new";
        if (HttpPatchSource.IsHttp(share))
        {
            var result = StageLauncherUpdateHttp(share, self, staged, out var reached);
            // Same fallback as UpdatePak: the public default may not be reachable from inside the LAN.
            var lanUrl = LanPatchUrl(server);
            if (!reached && IsValidServer(server) && string.Equals(share, DefaultShare(server), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(lanUrl, share, StringComparison.OrdinalIgnoreCase))
                result = StageLauncherUpdateHttp(lanUrl, self, staged, out _);
            return result;
        }
        try
        {
            var remote = Path.Combine(share, Program.LauncherExe);
            if (!File.Exists(remote))
                return null;
            var remoteTime = File.GetLastWriteTimeUtc(remote);
            var localTime = File.GetLastWriteTimeUtc(self);
            if (remoteTime <= localTime.AddSeconds(2))
                return null;
            File.Copy(remote, staged, true);
            _log($"A newer launcher ({remoteTime.ToLocalTime():yyyy-MM-dd HH:mm}, this one is from {localTime.ToLocalTime():yyyy-MM-dd HH:mm}) is on the share; click \"Restart to update\" to switch.");
            return staged;
        }
        catch (Exception exception)
        {
            _log("Launcher update check failed: " + exception.Message);
            return null;
        }
    }

    /// <summary>
    /// The patch-server variant: index.txt carries the published launcher's modification time; a newer one
    /// is downloaded (gzip, SHA-256 from the signed index) to "...exe.new" with that time, so after the swap
    /// the two agree. An index whose signature does not check out is never used.
    /// </summary>
    private string StageLauncherUpdateHttp(string url, string self, string staged, out bool reached)
    {
        reached = false;
        try
        {
            var source = new HttpPatchSource(url, _log);
            Dictionary<string, IndexEntry> index;
            try { index = source.GetSignedIndex(); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                return null; // not reachable; the caller may try the LAN address
            }
            reached = true;
            if (index == null || !index.TryGetValue(Program.LauncherExe, out var entry))
                return null;
            var localTime = File.GetLastWriteTimeUtc(self);
            if (entry.ModifiedUtc <= localTime.AddSeconds(2))
                return null;
            _log($"A newer launcher ({entry.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}, this one is from {localTime.ToLocalTime():yyyy-MM-dd HH:mm}) is on the patch server; downloading it ...");
            source.Download(entry, staged);
            _log("Click \"Restart to update\" to switch to the new launcher.");
            return staged;
        }
        catch (PatchTrustException exception)
        {
            _log($"Launcher update REFUSED: {exception.Message}; the launcher is not replaced.");
            return null;
        }
        catch (Exception exception)
        {
            _log("Launcher update check failed: " + exception.GetBaseException().Message);
            return null;
        }
    }

    /// <summary>Swaps the staged exe in after this process has exited and starts the new one. Caller exits right after.</summary>
    public void RestartToUpdate(string staged)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("own exe path unknown");
        // Full paths for cmd.exe and timeout.exe (see StoreShareCredential); /s makes cmd strip exactly the
        // outer quotes, so the quoted timeout path at the start of the command survives.
        var timeout = Path.Combine(Environment.SystemDirectory, "timeout.exe");
        var command = $"\"{timeout}\" /t 2 /nobreak >nul & move /y \"{staged}\" \"{self}\" & start \"\" \"{self}\"";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/s /c \"" + command + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RootDir,
        };
        Process.Start(info)?.Dispose();
        _log("Restarting with the new launcher ...");
    }
}
