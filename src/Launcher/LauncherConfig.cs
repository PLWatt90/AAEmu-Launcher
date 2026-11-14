// Server-specific settings of the launcher, so one source tree serves any AAEmu server.
//
// Where the values come from, later sources winning field by field:
//   1. the built-in defaults below
//   2. config\launcher.config.json of the repository, embedded into the exe at build time
//      (scripts\Build.ps1; falls back to config\launcher.config.example.json when it is missing)
//   3. launcher.config.json next to the exe, if present (lets a server owner ship a prebuilt exe
//      and configure it without rebuilding)
// Per-player choices (account name, server field, patch source, update box) stay in launcher.json.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AAEmuLauncher;

internal enum AuthMode
{
    /// <summary>
    /// The login server checks a per-account launcher password (and an optional PIN) and offers
    /// "Create account" / "Forgot password?" through the launcher (see docs/login-protocol.md).
    /// </summary>
    Password,

    /// <summary>
    /// Stock AAEmu: the login server trusts the launcher passport and logs in by account name alone.
    /// The launcher hides the password, PIN and account buttons.
    /// </summary>
    Trusted,
}

internal sealed class LauncherConfig
{
    public const string FileName = "launcher.config.json";
    private const string ResourceName = "AAEmuLauncher.launcher.config.json";

    /// <summary>Game name in the window title and the big heading.</summary>
    public string ProductName { get; set; } = "ArcheAge";

    /// <summary>Line under the heading (the version is appended).</summary>
    public string Subtitle { get; set; } = "Launcher";

    /// <summary>
    /// The exe name (without .exe) scripts\Build.ps1 gives the launcher; only read by the build. At run time
    /// the launcher uses its own assembly name (Program.LauncherExe).
    /// </summary>
    public string ExeName { get; set; } = "AAEmu-Launcher";

    /// <summary>The Server field's value for a player with no launcher.json yet (IP or host name of the login server).</summary>
    public string DefaultServer { get; set; } = "127.0.0.1";

    /// <summary>Earlier server addresses; a launcher.json still holding one moves to DefaultServer by itself.</summary>
    public string[] RetiredServers { get; set; } = [];

    /// <summary>TCP port of AAEmu.Login (1237 in a stock install).</summary>
    public int AuthPort { get; set; } = 1237;

    /// <summary>Password (servers with launcher passwords) or Trusted (stock AAEmu, name only).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AuthMode>))]
    public AuthMode AuthMode { get; set; } = AuthMode.Trusted;

    /// <summary>
    /// The patch source every player uses by default, e.g. "http://patch.example.com:8099/". Empty means
    /// "http://&lt;Server field&gt;:&lt;PatchPort&gt;/", i.e. the patch server runs on the login server's machine.
    /// </summary>
    public string PatchUrl { get; set; } = "";

    /// <summary>Port of the patch server (src/PatchServer) when PatchUrl is empty, and of the LAN fallback.</summary>
    public int PatchPort { get; set; } = 8099;

    /// <summary>
    /// Base64 SubjectPublicKeyInfo of the patch signing key ("aapatch keygen" prints it). Without it the
    /// launcher refuses every HTTP patch source, since it could not tell a real patch from a forged one.
    /// </summary>
    public string PatchPublicKey { get; set; } = "";

    /// <summary>Optional Windows file share (\\&lt;server&gt;\&lt;ShareName&gt;) tried when the patch server cannot be reached. Empty = no share fallback.</summary>
    public string ShareName { get; set; } = "";

    /// <summary>Account name the "Store share credential" button stores for the share.</summary>
    public string ShareUser { get; set; } = "aapatch";

    /// <summary>
    /// Locale written once into Documents\ArcheAge\system.cfg (e.g. "en_us"), for clients that would
    /// otherwise start in another language. Empty = leave system.cfg alone.
    /// </summary>
    public string ClientLocale { get; set; } = "";

    /// <summary>
    /// Folder under the client root that holds archeage.exe: "Bin64" (default) or e.g. "bin32" for 1.2 clients.
    /// </summary>
    public string ClientDir { get; set; } = "Bin64";

    /// <summary>
    /// archeage.exe command line template. Empty = the stock "-k passport +auth_..." line. Placeholders:
    /// {account} {server} {port} {token} {pinToken} {passport} (passport already quote-escaped).
    /// {root} and {clientExe} give the client's paths to a startExe (e.g. the 1.2 Rust aaemu-launcher).
    /// </summary>
    public string ClientArguments { get; set; } = "";

    /// <summary>
    /// Program Play starts instead of archeage.exe, relative to the client root (e.g. "aaemu-rs\aaemu-launcher.exe"
    /// for the 1.2 Rust launcher, which then starts the client). Empty = start archeage.exe directly.
    /// Placeholders {clientExe} and {root} in clientArguments give it the client's paths.
    /// </summary>
    public string StartExe { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The values in effect; problems while reading are listed in <see cref="Problems"/>.</summary>
    public static LauncherConfig Current { get; } = Load();

    /// <summary>Where each layer came from, for the log.</summary>
    public List<string> Sources { get; } = [];

    public List<string> Problems { get; } = [];

    private static LauncherConfig Load()
    {
        var config = new LauncherConfig();
        try
        {
            using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream(ResourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                config.Merge(reader.ReadToEnd(), "built-in config");
            }
        }
        catch (Exception exception)
        {
            config.Problems.Add("built-in config could not be read: " + exception.Message);
        }

        var side = Path.Combine(Program.ExeDir, FileName);
        if (File.Exists(side))
        {
            try { config.Merge(File.ReadAllText(side), side); }
            catch (Exception exception) { config.Problems.Add($"{side} could not be read ({exception.Message}); ignored."); }
        }
        config.Normalize();
        return config;
    }

    /// <summary>Copies every property the JSON names onto this object (missing ones keep their value).</summary>
    private void Merge(string json, string source)
    {
        var layer = JsonSerializer.Deserialize<LauncherConfig>(json, Options) ?? throw new JsonException("empty document");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var target = typeof(LauncherConfig).GetProperties()
                .FirstOrDefault(p => p.CanWrite && p.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                if (!property.Name.StartsWith('_') && !property.Name.StartsWith('$'))
                    Problems.Add($"{source}: unknown setting \"{property.Name}\" ignored.");
                continue;
            }
            target.SetValue(this, target.GetValue(layer));
        }
        Sources.Add(source);
    }

    private void Normalize()
    {
        ProductName = string.IsNullOrWhiteSpace(ProductName) ? "ArcheAge" : ProductName.Trim();
        Subtitle = (Subtitle ?? "").Trim();
        DefaultServer = (DefaultServer ?? "").Trim();
        RetiredServers ??= [];
        PatchUrl = (PatchUrl ?? "").Trim();
        if (PatchUrl.Length > 0 && !PatchUrl.EndsWith('/'))
            PatchUrl += "/";
        PatchPublicKey = (PatchPublicKey ?? "").Trim();
        ShareName = (ShareName ?? "").Trim().Trim('\\');
        ShareUser = string.IsNullOrWhiteSpace(ShareUser) ? "aapatch" : ShareUser.Trim();
        ClientLocale = (ClientLocale ?? "").Trim();
        ClientDir = (ClientDir ?? "").Trim().Trim('\\', '/');
        if (ClientDir.Length == 0 || ClientDir.Contains("..") || Path.IsPathRooted(ClientDir))
        {
            if (ClientDir.Length > 0) Problems.Add($"clientDir \"{ClientDir}\" is not a plain subfolder; using Bin64.");
            ClientDir = "Bin64";
        }
        ClientArguments = (ClientArguments ?? "").Trim();
        StartExe = (StartExe ?? "").Trim().Trim('\\', '/');
        if (StartExe.Length > 0 && (StartExe.Contains("..") || Path.IsPathRooted(StartExe)))
        {
            Problems.Add($"startExe \"{StartExe}\" must be a path inside the client folder; starting archeage.exe instead.");
            StartExe = "";
        }
        if (AuthPort is <= 0 or >= 65536)
        {
            Problems.Add($"authPort {AuthPort} is not a port; using 1237.");
            AuthPort = 1237;
        }
        if (PatchPort is <= 0 or >= 65536)
        {
            Problems.Add($"patchPort {PatchPort} is not a port; using 8099.");
            PatchPort = 8099;
        }
        if (PatchUrl.Length > 0 && !HttpPatchSource.IsHttp(PatchUrl))
        {
            Problems.Add($"patchUrl \"{PatchUrl}\" is not an http(s) URL; ignored.");
            PatchUrl = "";
        }
    }
}
