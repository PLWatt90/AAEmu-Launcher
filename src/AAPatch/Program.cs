// aapatch - keeps a player's game_pak in step with a small set of patched entries.
//
//   manifest <game_pak> <patchDir> <innerPath> [innerPath ...]
//       Server side. Exports the named entries from the pak into patchDir (keeping their
//       inner paths) and writes patchDir\manifest.txt with a patch id and one
//       "innerPath|size|md5" line per entry.
//
//   apply <game_pak> <patchDir>
//       Player side. If the pak already carries the manifest's id (a small marker entry),
//       nothing is read or written. Otherwise every entry whose size or md5 differs is
//       replaced from patchDir, the marker is written and the pak header saved.
//
//   status <game_pak> <patchDir>
//       Like apply but changes nothing. Exit 0 = up to date, 1 = updates pending.
//
//   keygen <privateKey.pem>
//       Server side, once. Creates the ECDSA P-256 patch signing key (refuses to overwrite one) and
//       prints its public key; put it into patchPublicKey of config/launcher.config.json.
//
//   sign <privateKey.pem> <file>
//       Server side. Writes <file>.sig: base64 ECDSA P-256 / SHA-256 signature (IEEE P1363) over the
//       file's bytes. Make-Patch.ps1 signs index.txt with it; the launcher refuses an unsigned index.
//
//   pubkey <privateKey.pem>
//       Prints the public key of an existing signing key (the launcher's patchPublicKey).
//
// Exit codes: 0 ok, 1 updates pending (status only), 2 error, 3 manifest missing, 64 usage.

using System.Security.Cryptography;
using System.Text;
using AAEmu.Commons.Utils.AAPak;

namespace AAPakPatch;

internal static class Program
{
    private const string MarkerEntry = "game/aaemu_patch.ver";
    private const string ManifestName = "manifest.txt";

    private sealed record Entry(string Name, long Size, string Md5);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return Usage();
            switch (args[0].ToLowerInvariant())
            {
                case "manifest":
                    return args.Length >= 4 ? Manifest(args[1], args[2], args[3..]) : Usage();
                case "apply":
                    return args.Length == 3 ? Apply(args[1], args[2], dryRun: false) : Usage();
                case "status":
                    return args.Length == 3 ? Apply(args[1], args[2], dryRun: true) : Usage();
                case "verify":
                    return args.Length == 3 ? Apply(args[1], args[2], dryRun: true, ignoreMarker: true) : Usage();
                case "keygen":
                    return args.Length == 2 ? KeyGen(args[1]) : Usage();
                case "sign":
                    return args.Length == 3 ? Sign(args[1], args[2]) : Usage();
                case "pubkey":
                    return args.Length == 2 ? PubKey(args[1]) : Usage();
                default:
                    return Usage();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("aapatch: " + exception.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("aapatch " + typeof(Program).Assembly.GetName().Version);
        Console.WriteLine("  manifest <game_pak> <patchDir> <innerPath> [innerPath ...]  export entries and write manifest.txt (server side)");
        Console.WriteLine("  apply    <game_pak> <patchDir>                              bring the pak up to date (player side)");
        Console.WriteLine("  status   <game_pak> <patchDir>                              report only; exit 0 = up to date, 1 = updates pending");
        Console.WriteLine("  verify   <game_pak> <patchDir>                              like status but ignores the marker and hashes every entry");
        Console.WriteLine("  keygen   <privateKey.pem>                                   create the patch signing key, print its public key");
        Console.WriteLine("  sign     <privateKey.pem> <file>                            write <file>.sig (ECDSA P-256 / SHA-256, base64)");
        Console.WriteLine("  pubkey   <privateKey.pem>                                   print the public key (patchPublicKey) of a signing key");
        return 64;
    }

    // ---------------------------------------------------------------- signing

    private static int KeyGen(string privateKeyPath)
    {
        if (File.Exists(privateKeyPath))
        {
            Console.Error.WriteLine($"aapatch: {privateKeyPath} already exists; not replacing a signing key " +
                                    "(every launcher carries the public key, so a new key needs a new launcher)");
            return 2;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privateKeyPath))!);
        File.WriteAllText(privateKeyPath, key.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
        Console.WriteLine($"private key written to {privateKeyPath} (keep it off the patch folder and back it up)");
        Console.WriteLine("public key: " + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static int Sign(string privateKeyPath, string file)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(privateKeyPath));
        var signature = key.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var sigPath = file + ".sig";
        File.WriteAllText(sigPath + ".tmp", Convert.ToBase64String(signature) + "\n", new UTF8Encoding(false));
        File.Move(sigPath + ".tmp", sigPath, overwrite: true);
        Console.WriteLine($"signed {file} -> {sigPath} (public key {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())[..16]}...)");
        return 0;
    }

    private static int PubKey(string privateKeyPath)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(privateKeyPath));
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    /// <summary>An inner path that stays inside the patch folder: relative, no drive, no "." or ".." segment.</summary>
    private static bool IsSafeInnerPath(string name) =>
        name.Length > 0 && !name.Contains(':') && !name.Contains('\\') && !name.StartsWith('/')
        && name.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    // ---------------------------------------------------------------- manifest

    private static int Manifest(string pakPath, string patchDir, string[] names)
    {
        var lines = new List<string>();
        var pak = OpenPak(pakPath, readOnly: true);
        try
        {
            Directory.CreateDirectory(patchDir);
            foreach (var raw in names)
            {
                var name = AAPak.ToPakSlashes(raw).TrimStart('/');
                if (!pak.GetFileByName(name, out var info))
                    throw new FileNotFoundException("entry not found in pak: " + name);

                var dest = Path.Combine(patchDir, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                string md5;
                using (var source = pak.ExportFileAsStream(info))
                using (var target = File.Create(dest))
                {
                    md5 = CopyAndHash(source, target);
                }
                File.SetLastWriteTime(dest, DateTime.FromFileTime(info.modifyTime));
                lines.Add($"{name}|{info.size}|{md5}");
                Console.WriteLine($"exported {name} ({info.size:N0} bytes, md5 {md5})");
            }
        }
        finally
        {
            pak.ClosePak();
        }

        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                 Md5Hex(MD5.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..8];
        var manifest = new StringBuilder();
        manifest.AppendLine("# AAPakPatch manifest: innerPath|size|md5");
        manifest.AppendLine("id=" + id);
        foreach (var line in lines)
            manifest.AppendLine(line);
        var manifestPath = Path.Combine(patchDir, ManifestName);
        File.WriteAllText(manifestPath, manifest.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"manifest {id} with {lines.Count} entries written to {manifestPath}");
        return 0;
    }

    // ---------------------------------------------------------------- apply / status

    private static int Apply(string pakPath, string patchDir, bool dryRun, bool ignoreMarker = false)
    {
        var manifestPath = Path.Combine(patchDir, ManifestName);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"aapatch: no {ManifestName} in {patchDir}");
            return 3;
        }

        var (id, entries) = ReadManifest(manifestPath);

        // Fast path: a pak that carries this manifest's marker needs no hashing at all.
        var pending = new List<Entry>();
        var probe = OpenPak(pakPath, readOnly: true);
        try
        {
            var marker = ReadMarker(probe);
            if (marker == id && !ignoreMarker)
            {
                Console.WriteLine($"up to date (patch {id})");
                return 0;
            }
            Console.WriteLine(marker == null ? "pak carries no patch marker" : $"pak has patch {marker}, manifest is {id}");
            Console.WriteLine($"pak: {pakPath} ({new FileInfo(pakPath).Length:N0} bytes, {probe.pakFiles.Count:N0} entries)");

            foreach (var entry in entries)
            {
                if (probe.GetFileByName(entry.Name, out var info) && info.size == entry.Size &&
                    info.md5 != null && Md5Hex(info.md5) == entry.Md5)
                {
                    if (ignoreMarker)
                    {
                        // verify: do not trust the table's stored hash, read the entry itself.
                        string actual;
                        using (var stream = probe.ExportFileAsStream(info))
                            actual = CopyAndHash(stream, Stream.Null);
                        if (actual != entry.Md5)
                        {
                            Console.WriteLine($"  CORRUPT  {entry.Name} (table says {entry.Md5}, data is {actual})");
                            pending.Add(entry);
                            continue;
                        }
                    }
                    Console.WriteLine("  ok       " + entry.Name + (ignoreMarker ? " (content hashed)" : ""));
                    continue;
                }
                Console.WriteLine($"  update   {entry.Name} ({entry.Size:N0} bytes)");
                pending.Add(entry);
            }
        }
        finally
        {
            probe.ClosePak();
        }

        if (dryRun)
        {
            Console.WriteLine(pending.Count == 0
                ? "status: all entries match, only the marker is missing"
                : $"status: {pending.Count} entries to update");
            return pending.Count == 0 ? 0 : 1;
        }

        // Check every source file before touching the pak, so a damaged or half-copied share
        // never leaves the pak half patched.
        foreach (var entry in pending)
        {
            var src = SourcePath(patchDir, entry);
            if (!File.Exists(src))
                throw new FileNotFoundException("patch file missing: " + src);
            var length = new FileInfo(src).Length;
            if (length != entry.Size)
                throw new InvalidDataException($"patch file size mismatch for {entry.Name}: {length} != {entry.Size}");
            using var fs = File.OpenRead(src);
            if (CopyAndHash(fs, Stream.Null) != entry.Md5)
                throw new InvalidDataException($"patch file hash mismatch for {entry.Name} (the copy on the share is damaged or still being updated)");
        }

        var pak = OpenPak(pakPath, readOnly: false);
        try
        {
            foreach (var entry in pending)
            {
                var src = SourcePath(patchDir, entry);
                // AddFileFromStream replaces an existing entry in place when the new data fits its
                // reserved space and otherwise frees it and appends.
                if (!pak.AddFileFromFile(src, entry.Name, true))
                    throw new IOException("could not write " + entry.Name + " into the pak");
                Console.WriteLine("  written  " + entry.Name);
            }
            WriteMarker(pak, id);
            pak.SaveHeader();
        }
        finally
        {
            pak.ClosePak();
        }

        Console.WriteLine($"done: {pending.Count} entries updated, pak marked as patch {id}");
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    private static AAPak OpenPak(string path, bool readOnly)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("pak not found: " + path);
        var pak = new AAPak(path, readOnly, false);
        if (!pak.isOpen)
            throw new IOException("could not open " + path + (readOnly ? "" : " for writing (is the game still running?)"));
        return pak;
    }

    private static (string Id, List<Entry> Entries) ReadManifest(string manifestPath)
    {
        string id = null;
        var entries = new List<Entry>();
        foreach (var rawLine in File.ReadAllLines(manifestPath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("id=", StringComparison.Ordinal))
            {
                id = line[3..].Trim();
                continue;
            }
            var parts = line.Split('|');
            if (parts.Length != 3 || !long.TryParse(parts[1], out var size))
                throw new InvalidDataException("bad manifest line: " + rawLine);
            var name = AAPak.ToPakSlashes(parts[0]).TrimStart('/');
            if (!IsSafeInnerPath(name))
                throw new InvalidDataException("manifest names a path outside the patch folder: " + rawLine);
            entries.Add(new Entry(name, size, parts[2].Trim().ToLowerInvariant()));
        }
        if (string.IsNullOrEmpty(id))
            throw new InvalidDataException("manifest has no id= line");
        if (entries.Count == 0)
            throw new InvalidDataException("manifest lists no entries");
        return (id, entries);
    }

    private static string SourcePath(string patchDir, Entry entry) =>
        Path.Combine(patchDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));

    private static string ReadMarker(AAPak pak)
    {
        if (!pak.GetFileByName(MarkerEntry, out var info))
            return null;
        using var stream = pak.ExportFileAsStream(info);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    private static void WriteMarker(AAPak pak, string id)
    {
        var temp = Path.Combine(Path.GetTempPath(), "aaemu_patch_" + Guid.NewGuid().ToString("N") + ".ver");
        File.WriteAllText(temp, id + "\n", new UTF8Encoding(false));
        try
        {
            if (!pak.AddFileFromFile(temp, MarkerEntry, true))
                throw new IOException("could not write the patch marker entry");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string CopyAndHash(Stream source, Stream target)
    {
        using var md5 = MD5.Create();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            target.Write(buffer, 0, read);
            md5.TransformBlock(buffer, 0, read, null, 0);
        }
        md5.TransformFinalBlock([], 0, 0);
        return Md5Hex(md5.Hash!);
    }

    private static string Md5Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
