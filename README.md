# AAEmu-Launcher

A Windows launcher and patch system for ArcheAge clients that play on an
[AAEmu](https://github.com/AAEmu) server.

- **Launcher** (`src/Launcher`): a small GUI that sits in the player's client folder. It updates
  `game_pak` from your server, then starts `Bin64\archeage.exe` already pointed at your login server.
  It updates itself too.
- **aapatch** (`src/AAPatch`): a command-line tool that writes changed entries into a `game_pak` and
  makes and checks patch manifests. The launcher runs it on player PCs. You run it on the server
  machine to publish.
- **Patch server** (`src/PatchServer`): a read-only HTTP file server for the patch folder. It supports
  resumable downloads.
- **Scripts** (`scripts/`): first-time setup, build, publish a patch, start the patch server.

Patches are **signed**. Every file a launcher downloads is checked against `index.txt`, which you sign
with a private key that never leaves your server machine. Plain HTTP is safe because a tampered
download is refused and never applied.

```
 server machine                                               player PC
 ┌──────────────────────────────┐                             ┌──────────────────────────────┐
 │ your edited game_pak         │                             │ <client>\AAEmu-Launcher.exe  │
 │   │ scripts\Make-Patch.ps1   │   http://server:8099/       │   │ 1. fetch + verify index  │
 │   ▼                          │ ──────────────────────────► │   │ 2. download changed files│
 │ patch folder (signed index)  │    aa-patch-server          │   │ 3. aapatch apply         │
 │   + aapatch.exe + launcher   │                             │   ▼ 4. start archeage.exe    │
 │ AAEmu.Login (port 1237) ◄────┼─────────────────────────────┼── login (account [+password])│
 └──────────────────────────────┘                             └──────────────────────────────┘
```

## Contents

- [Requirements](#requirements)
- [Server setup](#server-setup)
- [Publishing a patch](#publishing-a-patch)
- [Giving the launcher to players](#giving-the-launcher-to-players)
- [Login modes](#login-modes)
- [Network and firewall](#network-and-firewall)
- [Customizing the launcher](#customizing-the-launcher)
- [Testing](#testing)
- [Troubleshooting](#troubleshooting)
- More docs: [configuration](docs/configuration.md) · [player guide](docs/player-guide.md) ·
  [login protocol](docs/login-protocol.md) · [patch format](docs/patch-format.md)

## Requirements

| Where | What |
|---|---|
| Server machine (build + publish) | Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download), PowerShell 5.1 or 7 |
| Server machine (running the patch server) | .NET 10 runtime (comes with the SDK) |
| Player PCs | Windows 10/11 x64. Nothing else: the launcher and aapatch are self-contained exes |

You also need an ArcheAge client that works with your AAEmu server, and an AAEmu.Login server that
players can reach.

## Server setup

Do these steps once, on the machine that runs (or can reach) your AAEmu server.

**1. Get the code and run the setup script**

```powershell
git clone <this repository> AAEmu-Launcher
cd AAEmu-Launcher
powershell -ExecutionPolicy Bypass -File scripts\Setup-Server.ps1 -Server <your login server address>
```

The script:

- creates `config\launcher.config.json`, `config\publish.config.json` and `config\patch-entries.txt`
  from the `*.example` files (git ignores them, so your settings stay local)
- creates your **patch signing key** at `%LOCALAPPDATA%\AAEmu-Launcher\patch-signing-key.pem` and writes
  its public half into `patchPublicKey` of `launcher.config.json`
- builds everything into `out\`

> **Back up the signing key.** Every launcher you hand out trusts only this key. If you lose it,
> every player needs a new launcher. Never put it in the patch folder or in git (`*.pem` is ignored).

You can run the script again at any time. It keeps existing config files and keys.

**2. Edit `config\launcher.config.json`**

These settings are built into the launcher exe. The ones that matter most:

| Setting | Set it to |
|---|---|
| `defaultServer` | The address players use to reach AAEmu.Login (LAN IP, public IP or DNS name). |
| `authMode` | `"trusted"` for stock AAEmu, `"password"` if your login server implements launcher passwords. See [Login modes](#login-modes). |
| `patchUrl` | Leave empty if the patch server runs on the `defaultServer` machine. Set it to a full URL such as `http://patch.example.com:8099/` if players reach the patch server under another name. |
| `productName`, `subtitle` | The text in the window title and heading. |
| `exeName` | The launcher's file name. Pick it once, before players have the launcher: self-update looks for this name. |
| `clientLocale` | `"en_us"` (for example) if the client should be forced into a language on first start; otherwise leave empty. |

All settings are described in [docs/configuration.md](docs/configuration.md).

**3. Edit `config\publish.config.json` and `config\patch-entries.txt`**

- `pak`: the `game_pak` that holds your client changes (your "master" client on the server machine)
- `patchDir`: an empty folder that the patch server will serve, for example `C:\AAEmu\Patch`
- `patch-entries.txt`: every inner pak path your server changes, one per line, for example
  `game/db/compact.sqlite3` or `game/scriptsbin64/x2ui/.../some_window.alb`. When you add a new
  script file, also list the folder's `toc.g` that loads it.

If you don't patch the client at all yet, list just one entry (for example `game/db/compact.sqlite3`).
The launcher still needs a published folder to update itself from.

**4. Build, publish, serve**

```powershell
scripts\Build.ps1          # after every change to launcher.config.json
scripts\Make-Patch.ps1     # after every change to the pak (close the client first: it locks game_pak)
scripts\Start-PatchServer.bat
```

Keep the patch server window open (Ctrl+C stops it). To run it unattended, use Task Scheduler or a
service wrapper (for example NSSM) with:

```
out\patch-server\aa-patch-server.exe --root C:\AAEmu\Patch --port 8099
```

Opening `http://<server>:8099/` in a browser shows the current patch id and the launcher's file name.

## Publishing a patch

1. Change your master `game_pak` (with your usual pak tools), with the client closed.
2. Add any new inner paths to `config\patch-entries.txt`.
3. Run `scripts\Make-Patch.ps1`.

Players get the patch the next time they click **Play**. The launcher downloads only the entries
their pak lacks (large files travel gzipped and resume if interrupted), then `aapatch` writes them into
`game_pak` and stamps the pak with the patch id. The next start skips all hashing.

`Make-Patch.ps1` also publishes the current `aapatch.exe` and launcher. A rebuilt launcher (newer file
time) is offered to players as **Restart to update**.

## Giving the launcher to players

Send players `out\launcher\<exeName>.exe`, or tell them to download it from
`http://<server>:8099/<exeName>.exe`. They put it in the top folder of their client, next to `Bin64\`
and `game_pak`, and start it. Hand them [docs/player-guide.md](docs/player-guide.md) as well.

**Prebuilt exe without rebuilding.** The launcher also reads a `launcher.config.json` placed next to
the exe, overriding the built-in values one setting at a time. For example, to point a downloaded
launcher at another server:

```json
{ "defaultServer": "203.0.113.10", "patchPublicKey": "MFkw...", "authMode": "trusted" }
```

## Login modes

**`"trusted"` (stock AAEmu).** Upstream AAEmu.Login accepts a launcher login by account name alone:
it trusts the launcher passport and creates missing accounts. The launcher shows just **Account name**
and **Play**.

> Anyone who can reach your login port can then play as any account, GM accounts included. That is
> fine for a private LAN, not for a public server.

**`"password"`.** The launcher shows **Password** and **PIN** fields and the **Login**, **Create
account** and **Forgot password?** buttons. It never sends the password itself, only salted SHA-256
tokens. Your AAEmu.Login has to implement the protocol in
[docs/login-protocol.md](docs/login-protocol.md): store and check the tokens, and handle the
`register` / `recover` actions.

## Network and firewall

| Port | Who listens | Players need it |
|---|---|---|
| 1237/TCP (`authPort`) | AAEmu.Login | always |
| game server ports (see your AAEmu config) | AAEmu game/world server | always |
| 8099/TCP (`patchPort`) | aa-patch-server | for patching |

- Allow these ports in Windows Firewall on the server machine, for example:
  `New-NetFirewallRule -DisplayName "AAEmu patch server" -Direction Inbound -Protocol TCP -LocalPort 8099 -Action Allow`
- For players outside your network, forward the ports on your router and use a public address or DNS
  name for `defaultServer` / `patchUrl`.
- If LAN players can't reach your public name (no NAT loopback on the router), the launcher falls back
  by itself: from `patchUrl` to `http://<Server field>:<patchPort>/`, then to the optional Windows share.

**Optional Windows share fallback.** For LAN players you can also share the patch folder over SMB:
`scripts\New-PatchShareUser.ps1 -PatchDir C:\AAEmu\Patch` (elevated). Then set `shareName` /
`shareUser` in `launcher.config.json` and rebuild. A share source is trusted as it is (no signature),
so only use it on a network you control.

## Customizing the launcher

- **Text**: `productName`, `subtitle` in `launcher.config.json`.
- **Background**: put a `config\background.jpg` (around 1920×1080) next to the configs to replace the
  bundled art. The window crops it to fit.
- **Icon**: put a `config\launcher.ico` to give the exe and window your icon.
- **Code**: the window layout is built in code in `src/Launcher/MainForm.cs` (no designer). The account
  part is in `MainForm.Account.cs`, the patch logic in `Program.cs` (`LauncherCore`) and `HttpPatch.cs`.

Rebuild with `scripts\Build.ps1` and publish with `scripts\Make-Patch.ps1`.

**1.2 clients.** `clientDir`, `clientArguments` and `startExe` in `launcher.config.json` change where
`archeage.exe` is and how Play starts it: a 1.2 client sits in `bin32\` and is started through
AAEmu-1.2-Rust's `aaemu-launcher`. `looseFiles` in `publish.config.json` keeps loose client files up to
date as well, such as the 1.2 client's `game\db\compact.sqlite3`. The defaults keep the current
behaviour. Example in [docs/configuration.md](docs/configuration.md#example-12-clients).

**Launcher test modes** (console output, no window, no game start):

```
AAEmu-Launcher.exe --print-config                          settings in effect and their sources
AAEmu-Launcher.exe --test-status <pak> <url-or-folder>     what an update would do
AAEmu-Launcher.exe --test-update <pak> <url-or-folder>     run the update for real
AAEmu-Launcher.exe --print-args <account> <server> <password> [pin]
AAEmu-Launcher.exe --test-screenshot <file.png>
```

From PowerShell, start them with `Start-Process ... -Wait -NoNewWindow -RedirectStandardOutput out.txt`.
The launcher is a GUI exe, so PowerShell does not wait for it otherwise.

## Testing

```powershell
powershell -ExecutionPolicy Bypass -File tests\Run-Tests.ps1
```

This builds everything, makes a throwaway key and a test `game_pak`, publishes it, serves it on port
8199, and runs the launcher's update step against empty client paks. It covers a full update, the
no-op second run, `aapatch verify`, and refusal of a tampered index, a missing signature, a swapped
file, a wrong key, no key, a `.pem` request and signed `../` paths. It also covers loose client files
(from the patch server and from a folder source, including the size + time fast path), publishing a
second time with a changed `looseFiles` list, the `clientArguments` placeholders and the password
token formula. No game client is needed, and everything stays in `tests\work\`.

## Troubleshooting

| Launcher log says | Fix |
|---|---|
| `archeage.exe not found` | The launcher must sit in the client's top folder, next to `Bin64\` (or the folder `clientDir` names). |
| `Patch server ... not reachable` | Start the patch server; check the firewall/port forward; check `patchUrl` / the Server field. The game still starts, unpatched. |
| `answers but has no manifest.txt / index.txt` | Run `scripts\Make-Patch.ps1`. |
| `REFUSED: ... signature` | The launcher's `patchPublicKey` doesn't match the key that signed `index.txt`. Rebuild the launcher after `Setup-Server.ps1`, or you replaced the key. Players need the matching launcher. |
| `no patch signing key` | `patchPublicKey` is empty. Run `scripts\Setup-Server.ps1` and rebuild. |
| `does not match its signed index` / `publishing a new patch right now` | Make-Patch was running. Click Play again in a minute. |
| `could not open ... for writing` | The game (or another tool) still holds `game_pak`. Close it. |
| Login refused, reason 1 | Wrong account/password/PIN (password mode), or the server doesn't speak the launcher protocol: try `authMode: "trusted"`. |

The launcher keeps no log file. Click **Show log** in the window. Settings live in `launcher.json` next
to the exe. Passwords and PINs are never written anywhere.

## License and credits

GPL-3.0, see [LICENSE](LICENSE). The pak reader/writer in `src/AAPatch/AAPak` comes from AAEmu
(based on ZeromusXYZ's [AAEmu-Packer](https://github.com/ZeromusXYZ/AAEmu-Packer)), which is GPL
licensed. The bundled background art and the game name belong to their owners. Replace them with
your own (see [Customizing](#customizing-the-launcher)) if you distribute the launcher widely.
