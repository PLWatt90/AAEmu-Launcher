// MakeTestPak <path> [innerPath=localFile ...] - writes an empty game_pak, optionally with entries,
// for tests/Run-Tests.ps1.
using AAEmu.Commons.Utils.AAPak;

if (args.Length < 1)
{
    Console.WriteLine("usage: MakeTestPak <game_pak> [innerPath=localFile ...]");
    return 64;
}
if (File.Exists(args[0]))
    File.Delete(args[0]);
var pak = new AAPak(args[0], false, true);
if (!pak.isOpen)
{
    Console.WriteLine("could not create " + args[0]);
    return 2;
}
foreach (var spec in args.Skip(1))
{
    var cut = spec.IndexOf('=');
    if (cut <= 0 || !pak.AddFileFromFile(spec[(cut + 1)..], spec[..cut], true))
    {
        Console.WriteLine("could not add " + spec);
        return 2;
    }
}
pak.SaveHeader();
pak.ClosePak();
Console.WriteLine($"pak {args[0]} with {args.Length - 1} entries");
return 0;
