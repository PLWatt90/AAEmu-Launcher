# Player guide

## Install

1. Install the ArcheAge client your server owner told you to use.
2. Put the launcher exe (e.g. `AAEmu-Launcher.exe`) into the client's **top folder**, the one that holds
   `Bin64\` and `game_pak`. Don't put it inside `Bin64`.
3. Start the launcher. Nothing else needs installing.

Windows SmartScreen may warn about an unknown publisher the first time. Choose **More info → Run
anyway** if you trust your server owner.

## Play

- **Account name**: your account. On servers with passwords, also enter **Password** and, if your
  account has one, **PIN**, then click **Login**. Once the server accepts them, the button becomes
  **Play**.
- **Server**: filled in for you. Change it only if the server owner tells you to.
- **Patch source**: leave it as it is unless told otherwise.
- **Update game_pak before starting**: leave it ticked. The launcher downloads the server's client
  changes before each start. Unticking it applies to one start only.

The first start can take a while because large files download then. Later starts only fetch what
changed. An interrupted download resumes next time.

**Create account** / **Forgot password?** (servers with passwords only): pick an account name,
password, PIN and give your Discord username. You need the Discord username and PIN to set a new
password if you forget it.

## Updates

When a newer launcher is available, a **Restart to update** button appears. Click it.

## Problems

Click **Show log** for details.

- *archeage.exe not found*: the launcher is not in the client's top folder.
- *Patch server not reachable*: the server or your connection is down. The game starts anyway, but may
  miss the server's changes.
- *REFUSED*: the download didn't pass the security check. Try again in a minute. If it keeps
  happening, ask the server owner for a new launcher.
- *game_pak could not be updated*: close every running game client and click Play again.

Your password and PIN are never saved or sent in plain text. The launcher remembers only your account
name and server in `launcher.json` next to the exe.
