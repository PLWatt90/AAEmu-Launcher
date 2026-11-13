// HTTP patch source: the launcher's way to fetch the patch from the server's patch server
// (src/PatchServer, http://<server>:8099/ by default) instead of a folder or Windows share.
//
// The server publishes the same folder Make-Patch.ps1 writes, plus
//   index.txt          "path|size|sha256|gzSize|modifiedUtc" for every downloadable file and for
//                      manifest.txt itself (written last by Make-Patch, so a manifest.txt that
//                      disagrees with it means a patch is being published right now)
//   index.txt.sig      ECDSA P-256 / SHA-256 signature over index.txt, made with the private key
//                      that stays on the server machine (aapatch sign); PublicKey below checks it
//   <path>.gz          gzip copy of every large file (gzSize > 0 in the index)
// Nothing from the server is used unless the signed index vouches for it: plain HTTP can be
// tampered with on the way, the signature cannot be forged without the server's private key.
// The launcher downloads manifest.txt and only the entries aapatch reports as pending into
// <client>\patch-download, runs "aapatch apply" against that folder and deletes it afterwards.
// Interrupted downloads resume (HTTP Range) on the next start.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace AAEmuLauncher;

/// <summary>One downloadable file as index.txt lists it.</summary>
internal sealed record IndexEntry(string Path, long Size, string Sha256, long GzSize, DateTime ModifiedUtc);

/// <summary>The index failed its signature check, or vouches for something it cannot.</summary>
internal sealed class PatchTrustException(string message) : Exception(message);

internal sealed class HttpPatchSource
{
    public const string IndexName = "index.txt";
    public const string SignatureName = "index.txt.sig";

    /// <summary>
    /// Public half of the patch signing key (SubjectPublicKeyInfo, ECDSA P-256, base64), from the config's
    /// patchPublicKey. The private half stays on the server machine (aapatch keygen); a new key means a new
    /// launcher (or launcher.config.json) for every player, so keep the private key backed up.
    /// </summary>
    public static string PublicKey => Program.Config.PatchPublicKey;

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);
    /// <summary>A download that receives nothing for this long is given up.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    /// <summary>index.txt, manifest.txt and the signature are small; anything bigger is refused unread.</summary>
    private const int MaxTextBytes = 1 << 20;
    private static readonly HttpClient Client = CreateClient();

    private readonly Uri _base;
    private readonly Action<string> _log;

    public HttpPatchSource(string baseUrl, Action<string> log)
    {
        _base = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        _log = log ?? (_ => { });
    }

    public static bool IsHttp(string share) =>
        share != null && (share.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || share.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = QuickTimeout,
            AutomaticDecompression = DecompressionMethods.None, // .gz files are plain resources, unpacked here
            AllowAutoRedirect = false,                          // stay on the server the player named
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Path.GetFileNameWithoutExtension(Program.LauncherExe) + "/" + Program.VersionText);
        return client;
    }

    private Uri UrlOf(string path) =>
        new(_base, string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));

    // ---------------------------------------------------------------- paths

    /// <summary>A published path that stays inside the folder it is saved to: relative, no drive, no "." or "..".</summary>
    public static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrEmpty(path) && path.Length <= 260 && !path.Contains(':') && !path.Contains('\\')
        && !path.StartsWith('/') && path.All(c => c >= ' ')
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && part.Trim() == part && !part.EndsWith('.'));

    /// <summary>Where a published path lands under root; throws unless it is a file strictly inside root.</summary>
    public static string LocalPath(string root, string path)
    {
        if (!IsSafeRelativePath(path))
            throw new PatchTrustException("the patch server names an unsafe path: " + path);
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new PatchTrustException("the patch server names a path outside the download folder: " + path);
        return full;
    }

    // ---------------------------------------------------------------- small files

    /// <summary>A small file from the server; null on 404. Throws when the server cannot be reached.</summary>
    public byte[] GetBytes(string path)
    {
        using var cts = new CancellationTokenSource(QuickTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, UrlOf(path));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxTextBytes)
            throw new PatchTrustException($"{path} is larger than {MaxTextBytes:N0} bytes");
        using var body = response.Content.ReadAsStream(cts.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[1 << 16];
        int read;
        while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxTextBytes)
                throw new PatchTrustException($"{path} is larger than {MaxTextBytes:N0} bytes");
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// index.txt checked against index.txt.sig and the built-in public key, then parsed.
    /// Null when the server has no index (no patch published yet). Throws PatchTrustException when the
    /// signature is missing or wrong, and the usual exceptions when the server cannot be reached.
    /// </summary>
    public Dictionary<string, IndexEntry> GetSignedIndex()
    {
        var index = GetBytes(IndexName);
        if (index == null)
            return null;
        var signatureText = GetBytes(SignatureName);
        if (signatureText == null)
            throw new PatchTrustException($"the patch server's {IndexName} is not signed");
        byte[] signature;
        try { signature = Convert.FromBase64String(Encoding.ASCII.GetString(signatureText).Trim()); }
        catch (FormatException) { throw new PatchTrustException($"{SignatureName} is not a signature"); }
        if (string.IsNullOrEmpty(PublicKey))
            throw new PatchTrustException("this launcher has no patch signing key (patchPublicKey in launcher.config.json), so it cannot trust any patch server");
        using var key = ECDsa.Create();
        try { key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _); }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new PatchTrustException("the launcher's patchPublicKey is not a valid ECDSA public key");
        }
        if (!key.VerifyData(index, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new PatchTrustException($"the signature of {IndexName} does not match");
        return ParseIndex(Encoding.UTF8.GetString(index));
    }

    public static Dictionary<string, IndexEntry> ParseIndex(string text)
    {
        var entries = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('|');
            if (parts.Length != 5 || !long.TryParse(parts[1], out var size) || size < 0 || !long.TryParse(parts[3], out var gzSize) || gzSize < 0
                || parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)
                || !DateTime.TryParseExact(parts[4], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var modified))
                throw new PatchTrustException("bad index.txt line: " + line);
            var path = parts[0];
            if (!IsSafeRelativePath(path))
                throw new PatchTrustException("index.txt names an unsafe path: " + path);
            entries[path] = new IndexEntry(path, size, parts[2].ToLowerInvariant(), gzSize, modified);
        }
        return entries;
    }

    /// <summary>manifest.txt as aapatch reads it: the id and "innerPath|size|md5" entries (paths checked).</summary>
    public static (string Id, List<(string Name, long Size, string Md5)> Entries) ParseManifest(string text)
    {
        string id = null;
        var entries = new List<(string, long, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("id=", StringComparison.Ordinal))
            {
                id = line[3..].Trim();
                continue;
            }
            var parts = line.Split('|');
            if (parts.Length != 3 || !long.TryParse(parts[1], out var size))
                throw new InvalidDataException("bad manifest line: " + line);
            if (!IsSafeRelativePath(parts[0]))
                throw new PatchTrustException("manifest.txt names an unsafe path: " + parts[0]);
            entries.Add((parts[0], size, parts[2].Trim().ToLowerInvariant()));
        }
        if (string.IsNullOrEmpty(id) || entries.Count == 0)
            throw new InvalidDataException("manifest.txt has no id or no entries");
        return (id, entries);
    }

    // ---------------------------------------------------------------- hashing

    /// <summary>True when the file exists with exactly this size and SHA-256.</summary>
    public static bool FileMatches(string path, long size, string sha256)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != size)
            return false;
        using var stream = File.OpenRead(path);
        return Hex(SHA256.HashData(stream)) == sha256;
    }

    public static bool BytesMatch(byte[] data, IndexEntry entry) =>
        data.LongLength == entry.Size && Hex(SHA256.HashData(data)) == entry.Sha256;

    public static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>Bytes a download of this entry moves over the wire.</summary>
    public static long WireSize(IndexEntry entry) => entry.GzSize > 0 ? entry.GzSize : entry.Size;

    // ---------------------------------------------------------------- downloads

    /// <summary>
    /// Downloads one indexed file to target (via the .gz copy when there is one), checks size and SHA-256
    /// against the signed index, and swaps it in whole; the file gets the index's modification time. A
    /// target that already matches is left alone. A partial download (.part) is resumed. Throws on any failure.
    /// The caller picks target (a fixed name, or LocalPath under its download folder).
    /// </summary>
    public void Download(IndexEntry entry, string target)
    {
        if (FileMatches(target, entry.Size, entry.Sha256))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var gz = entry.GzSize > 0;
        var remotePath = gz ? entry.Path + ".gz" : entry.Path;
        var wireSize = WireSize(entry);
        var part = target + (gz ? ".gz.part" : ".part");

        FetchWithResume(remotePath, part, wireSize);

        var unpacked = target + ".tmp";
        string sha256;
        long length = 0;
        using (var input = File.OpenRead(part))
        using (var source = gz ? new GZipStream(input, CompressionMode.Decompress) : (Stream)input)
        using (var output = File.Create(unpacked))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                length += read;
                if (length > entry.Size) // a .gz that unpacks beyond the indexed size is refused at once
                    break;
                output.Write(buffer, 0, read);
                hasher.AppendData(buffer, 0, read);
            }
            sha256 = Hex(hasher.GetHashAndReset());
        }
        File.Delete(part);
        if (length != entry.Size || sha256 != entry.Sha256)
        {
            File.Delete(unpacked);
            throw new InvalidDataException($"{entry.Path} does not match the signed index (got {length:N0} bytes). " +
                                           "The server may be publishing a new patch; try again in a minute.");
        }
        File.Move(unpacked, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, entry.ModifiedUtc);
    }

    private void FetchWithResume(string remotePath, string part, long wireSize)
    {
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > wireSize)
        {
            File.Delete(part);
            have = 0;
        }
        if (have == wireSize && have > 0)
            return;

        using var request = new HttpRequestMessage(HttpMethod.Get, UrlOf(remotePath));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        using var cts = new CancellationTokenSource(QuickTimeout);
        using var response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        var resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
            have = 0;

        if (wireSize >= 1 << 20) // small files only show in the caller's summary
            _log($"Downloading {remotePath}{(resumed ? $" (resuming at {have / 1048576.0:N1} MB)" : "")}, {wireSize / 1048576.0:N1} MB ...");
        using var body = response.Content.ReadAsStream();
        using var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write);
        var buffer = new byte[1 << 16];
        var nextReport = wireSize >= 8 << 20 ? have + wireSize / 10 : long.MaxValue;
        var started = DateTime.UtcNow;
        var startBytes = have;
        while (true)
        {
            int read;
            using (var stall = new CancellationTokenSource(StallTimeout))
            {
                try { read = body.ReadAsync(buffer, stall.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { throw new TimeoutException($"download of {remotePath} stalled for {StallTimeout.TotalSeconds:0} s"); }
            }
            if (read == 0)
                break;
            if (have + read > wireSize) // never store more than the signed index announced
                throw new InvalidDataException($"{remotePath} is larger than the signed index says");
            output.Write(buffer, 0, read);
            have += read;
            if (have >= nextReport)
            {
                var seconds = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
                _log($"  {remotePath}: {100.0 * have / wireSize:N0}% ({(have - startBytes) / 1048576.0 / seconds:N1} MB/s)");
                nextReport += wireSize / 10;
            }
        }
        if (have != wireSize)
            throw new IOException($"download of {remotePath} ended at {have:N0} of {wireSize:N0} bytes; it resumes on the next start");
    }
}
