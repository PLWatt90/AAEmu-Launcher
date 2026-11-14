# Patch folder format and aapatch

## The patch folder

`scripts\Make-Patch.ps1` writes:

```
<patchDir>\
  manifest.txt          patch id + the pak entries (aapatch manifest)
  game\...\<entries>    the exported pak entries, at their inner paths
  game\...\<entry>.gz   gzip copy of every entry of 1 MB or more
  files\...\<file>      loose client files (looseFiles), at their path under the client root
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
4. Every index path starting with `files/` is a loose client file: `files/game/db/x` lands in
   `<client>\game\db\x` (resolved strictly inside the client folder). A file that differs is downloaded,
   checked against the index's size and SHA-256, swapped in whole and stamped with the index's
   `modifiedUtc`. See [the fast path](#loose-files-the-fast-path) for how "differs" is decided.
5. `aapatch status` lists which entries the local pak lacks. Only those are downloaded into
   `<client>\patch-download\` (resuming `.part` files over HTTP Range). Each is checked against the
   index's size and SHA-256 after unpacking.
6. `aapatch apply` hashes every downloaded file against the manifest again, and only then writes into
   `game_pak`. The folder is deleted afterwards.

### Loose files: the fast path

Hashing a 400 MB loose file takes a second or more, so the launcher does not do it on every Play.
A loose file on the player's PC counts as current, without being read, when

- its size equals the index's size, and
- its last-write time equals the index's `modifiedUtc` (2 s tolerance), which is the time the launcher
  stamped it with when it put the file there.

Any other file is hashed. If the SHA-256 matches, the file is kept and stamped, so the next start is
fast again; if not, it is downloaded.

What this trusts is the player's own disk, nothing else: a loose file changed on the PC by something
that keeps both its size and its time is not noticed. Deleting the file, or changing its time, makes
the launcher check it properly again. Nothing that comes from a patch source is accepted this way;
every downloaded or copied file is still checked by size and SHA-256 before it replaces anything.

For the server side this means a new version of a loose file must differ in size or time from the one
published before. `Make-Patch.ps1` compares loose files by content, and gives a changed file a new
time if it would otherwise keep the old size and time.

### Folder and `\\share` sources

A folder or `\\share` source is **not signed**: steps 1 and 3 do not apply, and whoever can write to the
folder decides what players get. Only use one on a network you control.

- `game_pak`: `aapatch apply` reads the folder directly (steps 5 and 6 without the download). It still
  checks the MD5s against the manifest, but the manifest itself is not signed.
- Loose files: when the folder holds an `index.txt`, the launcher reads it without a signature check and
  handles its `files/...` entries as in step 4, copying from `<folder>\files\...` instead of
  downloading. The path rules of step 2, the confinement to the client folder, the fast path and the
  size + SHA-256 check against that `index.txt` all apply. A copy that does not match the folder's
  index stops the update and the game is not started.
- A folder with a `files\` folder but no `index.txt` gets one log line saying the loose files were not
  updated; `game_pak` is still patched.

## Publishing over a live folder

`Make-Patch.ps1` keeps the folder usable while it runs: it copies only loose files that are new or
changed, leaves unchanged ones and their `.gz` alone, writes `index.txt` and `index.txt.sig` last, and
only then deletes files under `files\` that are no longer published. A player who clicks Play during
a publish gets "try again in a minute" for the files that are changing, not for the rest.

## aapatch commands

```
aapatch manifest <game_pak> <patchDir> <innerPath|@listFile> [...]   export entries + write manifest.txt (server)
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
