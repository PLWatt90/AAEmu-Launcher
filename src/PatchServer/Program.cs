// aa-patch-server - serves the player patch folder (written by scripts\Make-Patch.ps1) over plain
// HTTP so the launcher can download patches. The launcher checks every file it uses against the
// signed index.txt, so tampering on the way is detected even without HTTPS.
//
//   aa-patch-server --root <patchDir> [--port <port>] [--name <title>]      default port 8099
//   (the root may also come from the AAPATCH_ROOT environment variable)
//
// Read-only: GET and HEAD only, files under the root only (no directory listing, no dot-files),
// HTTP Range supported so interrupted downloads resume. "/" answers with a short status page
// naming the current patch and the launcher download link.
// Players outside the LAN need TCP <port> forwarded on the router and allowed in the firewall.

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

string[] BlockedSuffixes = [".pem", ".key", ".pfx", ".tmp", ".part"];
var rootArg = Arg("--root") ?? Environment.GetEnvironmentVariable("AAPATCH_ROOT");
if (string.IsNullOrWhiteSpace(rootArg))
{
    Console.Error.WriteLine("usage: aa-patch-server --root <patchDir> [--port <port>] [--name <title>]");
    return 64;
}
var root = Path.GetFullPath(rootArg);
var title = Arg("--name") ?? "AAEmu patch server";
var port = int.TryParse(Arg("--port"), out var p) && p is > 0 and < 65536 ? p : 8099;
if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"patch folder {root} not found");
    return 2;
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseKestrel(options => options.ListenAnyIP(port));
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return;
    }
    // Never hand out key material or half-written files, even if one lands in the folder by mistake.
    var requested = context.Request.Path.Value ?? "";
    if (BlockedSuffixes.Any(s => requested.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {context.Connection.RemoteIpAddress} {context.Request.Method} {requested} -> 404 (blocked)");
        return;
    }
    await next();
    var range = context.Request.Headers.Range.ToString();
    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {context.Connection.RemoteIpAddress} {context.Request.Method} " +
                      $"{context.Request.Path}{(range.Length > 0 ? " " + range : "")} -> {context.Response.StatusCode}");
});

var types = new FileExtensionContentTypeProvider();
types.Mappings[".gz"] = "application/octet-stream"; // a plain file to the launcher, never a Content-Encoding
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(root),
    ContentTypeProvider = types,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/", () =>
{
    var manifest = Path.Combine(root, "manifest.txt");
    var id = File.Exists(manifest)
        ? File.ReadLines(manifest).FirstOrDefault(l => l.StartsWith("id=", StringComparison.Ordinal))?[3..] ?? "?"
        : "none (run Make-Patch.ps1)";
    // The launcher is whichever .exe the index lists besides aapatch.exe.
    var index = Path.Combine(root, "index.txt");
    var launcher = File.Exists(index)
        ? File.ReadLines(index).Select(l => l.Split('|')[0])
            .FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !f.Equals("aapatch.exe", StringComparison.OrdinalIgnoreCase))
        : null;
    return Results.Text($"{title}\npatch: {id}\n" + (launcher != null ? $"launcher: /{launcher}\n" : ""));
});

Console.WriteLine($"aa-patch-server: serving {root} on http://0.0.0.0:{port}/ (Ctrl+C stops)");
app.Run();
return 0;

string Arg(string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
