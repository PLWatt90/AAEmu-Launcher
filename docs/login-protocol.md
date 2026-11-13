# Launcher login protocol (`authMode: "password"`)

What an AAEmu.Login server has to implement so the launcher's password, PIN, **Create account** and
**Forgot password?** work. With `authMode: "trusted"` none of this is needed: stock AAEmu logs in by
the passport's account name.

## Transport

The launcher uses the client's own web-auth packet on the login port (`authPort`, 1237 by default),
which AAEmu.Login already parses (`CARequestWebAuthPacket`, opcode `0x002`):

```
u16 size | u16 type=0x002 | u32 pFrom=0 | u32 pTo=0 | u8 svc=0 | u8 dev=0 |
u16 len + UTF-8 passport JSON | 8 bytes mac | 8 bytes mac2 | u64 cpu | u8 is64Bit=1 | u8 isMultiClient | u8 clientSerial
```

The launcher reads replies until it sees:

- `ACJoinResponse` (`0x000`): accepted
- `ACLoginDenied` (`0x00c`): `u8 reason`, `u16 len + bytes` (vp, skipped), `u16 len + UTF-8 message`, `u8 quit`.
  A non-empty message is shown to the player as it is. Otherwise the launcher shows a text for the
  reason (1 = wrong credentials, 3 = already logged in, 5+ = blocked).

The **game client** sends the same packet with the passport the launcher hands it (`-k "<passport>"`),
so a login passes the same check twice: once for the launcher's Login button, once when the game starts.

## Passport JSON

Login (Login button, and the game client):

```json
{"source":"launcher","strUserToken":"v1:<hex>","strPinToken":"p1:<hex>|empty","StrUserName":"Alice",
 "serverId":"1","selectedServerId":"1","channelId":"1","subchannelId":"1","gameId":"1",
 "sIp":"<server>","sIps":"<server>","sPort":"1237","PASSPORTSVRIP":"<server>","extension":""}
```

Create account / Forgot password:

```json
{"source":"launcher","action":"register","StrUserName":"Alice","strUserToken":"v1:<hex>",
 "strPinToken":"p1:<hex>","discordName":"some.player"}
```

`action` is `"register"` or `"recover"`. For `recover`, `strUserToken` is the token of the **new**
password.

## Tokens

The launcher never sends a password or PIN, only these tokens (lower-case hex SHA-256 over UTF-8):

```
strUserToken = "v1:" + sha256("aaemu-launcher|"     + lower(account) + "|" + password)
strPinToken  = "p1:" + sha256("aaemu-launcher-pin|" + lower(account) + "|" + pin)
```

Test vector: account `Alice`, password `correct horse` gives
`v1:` + sha256(`aaemu-launcher|alice|correct horse`). `tests\Run-Tests.ps1` checks the launcher against
this formula, and `AAEmu-Launcher.exe --print-token <account> <password>` prints one.

Treat the token like a password: store it **hashed** (for example PBKDF2 through AAEmu's password
service), never in plain text. Accept only well-formed tokens (`^v1:[0-9a-f]{64}$`,
`^p1:[0-9a-f]{64}$`) and refuse anything else (old launchers sent a fixed `testtoken`).

Launcher-side input rules: account `^[A-Za-z0-9]{1,32}$`, password at least 4 characters, PIN 4–8
digits, Discord username trimmed, leading `@` dropped, lower-cased, `^[a-z0-9_.]{2,32}$` without `..`.
The server should enforce the same rules.

## Server behaviour (the reference implementation)

The server this launcher was written for behaves like this. Your rules may differ, but the launcher
expects a verdict packet for every request.

**Login**

| Account state | Result |
|---|---|
| no such account | denied: "There is no account with that name. Use "Create account" in the launcher." |
| no launcher password yet | denied: ask the owner to set one (an admin command sets it) |
| wrong password token | denied, reason 1 |
| account has a PIN and the passport has none | denied: the launcher is outdated |
| account has a PIN and it is wrong | denied, reason 1 |
| banned | denied with the ban reason |
| otherwise | accepted |

A login never creates an account or sets a password.

**Register**: refuse a taken account name or Discord username, otherwise create the account with
the hashed password token, hashed PIN token and Discord name, and answer `ACJoinResponse`.

**Recover**: if the account name, Discord username and PIN token all match, replace the password
token and answer `ACJoinResponse`; otherwise deny with a generic message (don't reveal which part was
wrong). Rate-limit Login/Register/Recover per IP and per account; the reference server does.

**Database** (MySQL `users` table in the reference server):

```sql
ALTER TABLE `users`
  ADD COLUMN `launcher_password_set` TINYINT(1) NOT NULL DEFAULT '0',
  ADD COLUMN `discord_name` VARCHAR(32) DEFAULT NULL,
  ADD COLUMN `launcher_pin` TEXT DEFAULT NULL,
  ADD UNIQUE KEY `discord_name` (`discord_name`);
```

The hashed password token goes into the existing `password` column.

## Where to hook it into AAEmu.Login

In upstream AAEmu, `CARequestWebAuthPacketHandler` extracts `StrUserName` from the passport and runs
`TokenAuthFlow`, which calls `LoginController.LoginTrusted` (name only, account auto-created). To
support this protocol:

1. Parse the whole passport (`StrUserName`, `strUserToken`, `strPinToken`, `discordName`, `action`).
2. In the token flow, dispatch on `action` to your Login / Register / Recover methods instead of
   `LoginTrusted`.
3. Return `AuthFlowResult.Success` or `AuthFlowResult.Denied(reason, message)`. The login session
   already turns those into `ACJoinResponse` / `ACLoginDenied`.
