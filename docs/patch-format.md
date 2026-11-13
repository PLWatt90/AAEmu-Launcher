# Patch folder format and aapatch

## The patch folder

`scripts\Make-Patch.ps1` writes:

```
<patchDir>\
  manifest.txt          patch id + the pak entries (aapatch manifest)
  game\...\<entries>    the exported pak entries, at their inner paths
  game\...\<entry>.gz   gzip copy of every entry of 1 MB or more
  aapatch.exe
  <exeName>.exe         the launcher
  index.txt             every downloadable file, written after everything else
  index.txt.sig         signature over index.txt
```

**manifest.txt**

```
# AAPakPatch manifest: innerPath|size|md5
id=20260101-120000-1a2b3c4d
game/db/compact.sqlite3|442368000|<md5>
```

The id is the time plus the first 8 hex digits of an MD5 over the entry lines.

**index.txt**

```
# patch server index: path|size|sha256|gzSize|modifiedUtc (signed: index.txt.sig)
game/db/compact.sqlite3|442368000|<sha256>|108000000|20260101115959
manifest.txt|123|<sha256>|0|20260101120000
aapatch.exe|...|...|...|...
AAEmu-Launcher.exe|...|...|...|...
```

`gzSize` > 0 means `<path>.gz` exists and is what the launcher downloads. `modifiedUtc`
(`yyyyMMddHHmmss`) of the launcher entry drives self-update: a launcher older than it downloads the new
one.

**index.txt.sig** is a base64 ECDSA P-256 signature over the exact bytes of `index.txt`, with SHA-256
in IEEE P1363 format (64 bytes).

## What the launcher checks

1. `index.txt.sig` verifies against the built-in `patchPublicKey`. If it doesn't, the launcher shows
   **REFUSED**, uses nothing and doesn't start the game.
2. Every path in the index and the manifest is relative, has no drive, no `\`, no `.`/`..` segment and
   no control characters.
3. `manifest.txt` matches its index entry (size + SHA-256), and every manifest entry is in the index
   with the same size. Otherwise a patch is being published right now: "try again in a minute".
4. `aapatch status` lists which entries the local pak lacks. Only those are downloaded into
   `<client>\patch-download\` (resuming `.part` files over HTTP Range). Each is checked against the
   index's size and SHA-256 after unpacking.
5. `aapatch apply` hashes every downloaded file against the manifest again, and only then writes into
   `game_pak`. The folder is deleted afterwards.

A folder or `\\share` source skips steps 1–4: `aapatch apply` reads it directly. It still checks the
MD5s against the manifest, but the manifest itself is not signed.

## aapatch commands

```
aapatch manifest <game_pak> <patchDir> <innerPath> [...]   export entries + write manifest.txt (server)
aapatch apply    <game_pak> <patchDir>                     bring a pak up to date (player)
aapatch status   <game_pak> <patchDir>                     dry run: exit 0 up to date, 1 pending
aapatch verify   <game_pak> <patchDir>                     like status, ignores the marker, hashes contents
aapatch keygen   <key.pem>                                 create a signing key (refuses to overwrite)
aapatch pubkey   <key.pem>                                 print its public key (patchPublicKey)
aapatch sign     <key.pem> <file>                          write <file>.sig
```

Exit codes: 0 ok, 1 updates pending (status/verify), 2 error, 3 no manifest.txt, 64 usage.

`apply` writes a marker entry `game/aaemu_patch.ver` holding the patch id into the pak. When the id
matches, later runs return at once without hashing. Entries that fit their old slot are rewritten in
place. Larger ones are appended and the old space is freed. Every source file is checked before the
pak is opened for writing, so a damaged download never leaves the pak half patched.
