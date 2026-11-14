# Configuration reference

There are three kinds of settings, kept apart on purpose:

| File | Used by | Contains | In git |
|---|---|---|---|
| `config/launcher.config.json` | the launcher (embedded at build) | server address, ports, login mode, patch key | no (template: `.example`) |
| `config/publish.config.json` + `config/patch-entries.txt` | `scripts\Make-Patch.ps1` on the server | local paths, what to publish | no (templates: `.example`) |
| `launcher.json` next to a player's exe | the launcher | the player's account name, Server field, patch source, update box | written by the launcher |

The JSON files allow whole-line `//` comments.

## launcher.config.json

Where the launcher reads it from (later sources win, one setting at a time):

1. the defaults in `src/Launcher/LauncherConfig.cs`
2. `config/launcher.config.json` (or the `.example` if it's missing), embedded by the build
3. `launcher.config.json` next to the exe at run time

`AAEmu-Launcher.exe --print-config` prints the result and the sources it came from. The launcher log
also lists the sources, and warns about unknown setting names.

| Setting | Default | Meaning |
|---|---|---|
| `productName` | `"ArcheAge"` | Heading and window title. |
| `subtitle` | `"Launcher"` | Line under the heading; the version is appended. |
| `exeName` | `"AAEmu-Launcher"` | File name of the built exe (read by `scripts\Build.ps1`). Self-update looks for this name in the patch folder, so don't change it once players have the launcher. If you must, publish both names for a while. |
| `defaultServer` | `"127.0.0.1"` | Pre-filled **Server** field: the AAEmu.Login address, also used as the game's auth address. |
| `retiredServers` | `[]` | Old addresses. A player whose `launcher.json` holds one is moved to `defaultServer` (useful after the server's IP changes). |
| `authPort` | `1237` | AAEmu.Login port. The `AAEMU_LAUNCHER_AUTH_PORT` environment variable overrides it (for testing). |
| `authMode` | `"trusted"` | `"trusted"` (stock AAEmu, name only) or `"password"` ([login protocol](login-protocol.md)). |
| `patchUrl` | `""` | Default patch source for every player. Empty = `http://<Server field>:<patchPort>/`. |
| `patchPort` | `8099` | Patch server port for the empty-`patchUrl` case and the fallback. |
| `patchPublicKey` | `""` | Base64 public key (`aapatch pubkey <key.pem>`). Required: without it every HTTP source is refused. |
| `shareName` | `""` | Windows share for the fallback `\\<server>\<shareName>`. Empty = no share fallback, and the **Store share credential** button is hidden. |
| `shareUser` | `"aapatch"` | Account name the credential button stores (`cmdkey`). |
| `clientLocale` | `""` | If set (e.g. `"en_us"`), the first Play writes `Documents\ArcheAge\system.cfg` with only `locale = <value>`, keeping the old file as `system.cfg.before-lan`. It runs once per PC (marker `system.cfg.lan-locale`). `AACFGDIR` overrides the folder. |
| `clientDir` | `"Bin64"` | Folder under the client root that holds `archeage.exe`, e.g. `"bin32"` for 1.2 clients. A plain subfolder: no `..`, no drive. |
| `clientArguments` | `""` | The command line Play passes. Empty = the stock `-k "<passport>" +auth_serveraddr ...` line. Placeholders: `{account}` `{server}` `{port}` (= `authPort`) `{token}` `{pinToken}` `{passport}` (quotes already escaped) `{root}` (client root) `{clientExe}` (full path of `<clientDir>\archeage.exe`). |
| `startExe` | `""` | Program Play starts instead of `archeage.exe`, relative to the client root (for a client that must be started through another launcher). It gets `clientArguments`; its folder is the working directory. Inside the client folder only. |

### Example: 1.2 clients

1.2 clients on [AAEmu-1.2-Rust](https://github.com/AAEmu/AAEmu-1.2-Rust), started through its `aaemu-launcher`
(login ticket + `aadriver`). Publish `aaemu-launcher.exe` and its `aadriver\` folder as loose files under
`aaemu-rs/` (see `looseFiles` below), plus a small `start.cmd` that runs
`"%~dp0aaemu-launcher.exe" %*` (it prompts for the password):

```json
{ "authPort": 1237, "clientDir": "bin32", "startExe": "aaemu-rs\\start.cmd",
  "clientArguments": "--client \"{clientExe}\" --login {server}:{port} --user {account} --lang en_us --sound" }
```

### Patch source order

When the player clicks Play, the launcher tries:

1. the **Patch source** field (default: `patchUrl`, or `http://<Server>:<patchPort>/`)
2. only if that was the default and could not be **reached**: `http://<Server>:<patchPort>/`
3. then `\\<Server>\<shareName>` if `shareName` is set

A source that answers with a bad signature never falls back; the game is not started. If no source can
be reached, the game starts without updating. A player can type any URL, local folder or `\\server\share`
into the Patch source field. An empty field means the default.

A folder or `\\server\share` source is not signed. It updates `game_pak` and, when it holds the
`index.txt` Make-Patch writes, the loose client files too (see
[patch format](patch-format.md#folder-and-share-sources)).

## publish.config.json

| Setting | Meaning |
|---|---|
| `pak` | The `game_pak` that holds your changes. |
| `patchDir` | Folder the patch server serves. Make-Patch creates it and writes into it. |
| `signingKey` | Private key. Empty = `%LOCALAPPDATA%\AAEmu-Launcher\patch-signing-key.pem`. Make-Patch refuses a key inside `patchDir`. |
| `entriesFile` | Inner pak paths to publish, relative to the config folder. Default `patch-entries.txt`. |
| `extraFiles` | Files copied into `patchDir` as they are (e.g. a player README). Not part of the pak patch or the signed index. |
| `copyToClients` | Client folders on this machine that should also get the fresh `aapatch.exe` and launcher. |
| `looseFiles` | Loose client files, outside `game_pak`: `[{ "from": "<file or folder here>", "to": "<path under the client root>" }]`. Published under `files/<to>` in the signed index; the launcher writes them into the client folder. For data the client reads loose (e.g. the 1.2 client's `game/db/compact.sqlite3`) or helper programs. Make-Patch copies only new or changed files into `patchDir\files` and removes the ones you took off the list after the new index is written. |

The entries file may be long: Make-Patch hands it to `aapatch manifest` as `@<list file>`, so the
Windows command-line limit does not apply.

`Make-Patch.ps1` parameters (`-Pak`, `-PatchDir`, `-SigningKey`, `-Entries`, `-Config`, `-NoBuild`)
override the file.

## Patch server

```
aa-patch-server --root <patchDir> [--port 8099] [--name "<title>"]
```

The root can also come from the `AAPATCH_ROOT` environment variable. The server answers GET and HEAD
only, sends no directory listings, refuses `.pem`/`.key`/`.pfx`/`.tmp`/`.part` files, and supports
HTTP Range. `scripts\Start-PatchServer.ps1` fills in the root and port from the configs.

## Rotating the signing key

1. `aapatch keygen <new.pem>`, then put its `aapatch pubkey` output into `patchPublicKey`.
2. Rebuild the launcher and give it to every player **by hand**. Old launchers refuse indexes signed
   with the new key, so they can't update themselves to the new launcher.
3. Point `signingKey` at the new key and run Make-Patch.
