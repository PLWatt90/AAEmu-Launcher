// Talks to the AAEmu login server (port 1237) the way the game client does for a launcher login: one
// CARequestWebAuth packet carrying the launcher passport JSON, then the server's first answer
// (ACJoinResponse = accepted, ACLoginDenied = refused with a reason and an optional message).
// The same packet with "action":"register" / "recover" in the passport creates an account or sets a
// new password (AAEmu.Login TokenAuthFlow). Nothing secret goes over the wire but the hashed tokens.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AAEmuLauncher;

internal enum AuthOutcome { Accepted, Denied, Unreachable, Failed }

internal sealed record AuthReply(AuthOutcome Outcome, int Reason, string Message);

internal static class AuthClient
{
    private const ushort CARequestWebAuth = 0x002;
    private const ushort ACJoinResponse = 0x000;
    private const ushort ACLoginDenied = 0x00c;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15); // the server hashes with PBKDF2

    /// <summary>Checks account, password and PIN without starting the game.</summary>
    public static AuthReply Login(string server, string account, string password, string pin) =>
        Send(server, LauncherCore.BuildPassport(account, server, LauncherCore.BuildToken(account, password),
            pin.Length > 0 ? LauncherCore.BuildPinToken(account, pin) : ""));

    /// <summary>Creates a new account (the server refuses a taken account name or Discord username).</summary>
    public static AuthReply Register(string server, string account, string password, string pin, string discord) =>
        Send(server, ActionPassport("register", account, LauncherCore.BuildToken(account, password),
            LauncherCore.BuildPinToken(account, pin), discord));

    /// <summary>Sets a new password when account name, Discord username and PIN match.</summary>
    public static AuthReply Recover(string server, string account, string newPassword, string pin, string discord) =>
        Send(server, ActionPassport("recover", account, LauncherCore.BuildToken(account, newPassword),
            LauncherCore.BuildPinToken(account, pin), discord));

    private static string ActionPassport(string action, string account, string token, string pinToken, string discord) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["source"] = "launcher",
            ["action"] = action,
            ["StrUserName"] = account,
            ["strUserToken"] = token,
            ["strPinToken"] = pinToken,
            ["discordName"] = discord,
        });

    private static AuthReply Send(string server, string passport)
    {
        using var client = new TcpClient();
        try
        {
            using (var connect = new CancellationTokenSource(ConnectTimeout))
                client.ConnectAsync(server, Program.AuthPort, connect.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return new AuthReply(AuthOutcome.Unreachable, 0,
                $"The login server {server}:{Program.AuthPort} could not be reached ({(exception is OperationCanceledException ? "timed out" : exception.Message)}).");
        }

        try
        {
            client.ReceiveTimeout = (int)ReplyTimeout.TotalMilliseconds;
            client.SendTimeout = (int)ConnectTimeout.TotalMilliseconds;
            var stream = client.GetStream();
            stream.Write(BuildRequest(passport));

            while (true)
            {
                var body = ReadFrame(stream);
                var type = BitConverter.ToUInt16(body, 0);
                if (type == ACJoinResponse)
                    return new AuthReply(AuthOutcome.Accepted, 0, "");
                if (type == ACLoginDenied)
                    return ReadDenied(body);
                // Anything else (a 2FA prompt the launcher does not handle, ...): keep reading until a verdict.
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or EndOfStreamException or ArgumentException)
        {
            return new AuthReply(AuthOutcome.Failed, 0, $"The login server did not answer properly ({exception.Message}).");
        }
    }

    /// <summary>[u16 size][u16 type][CARequestWebAuth body], little-endian, as AAEmu.Login reads it.</summary>
    private static byte[] BuildRequest(string passport)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            var auth = Encoding.UTF8.GetBytes(passport);
            writer.Write(CARequestWebAuth);
            writer.Write(0u);                 // pFrom
            writer.Write(0u);                 // pTo
            writer.Write((byte)0);            // svc
            writer.Write((byte)0);            // dev
            writer.Write((short)auth.Length); // auth: u16 length + UTF-8
            writer.Write(auth);
            writer.Write(new byte[8]);        // mac
            writer.Write(new byte[8]);        // mac2
            writer.Write(0UL);                // cpu
            writer.Write((byte)1);            // is64Bit
            writer.Write((byte)0);            // isMultiClient
            writer.Write((byte)0);            // clientSerial
        }
        var payload = body.ToArray();
        var frame = new byte[payload.Length + 2];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 2), (ushort)payload.Length);
        payload.CopyTo(frame, 2);
        return frame;
    }

    private static byte[] ReadFrame(NetworkStream stream)
    {
        var size = new byte[2];
        stream.ReadExactly(size);
        var length = BitConverter.ToUInt16(size, 0);
        if (length < 2)
            throw new IOException($"bad packet size {length}");
        var body = new byte[length];
        stream.ReadExactly(body);
        return body;
    }

    /// <summary>ACLoginDenied: u8 reason, u16-sized vp bytes, u16-sized UTF-8 message, u8 quit.</summary>
    private static AuthReply ReadDenied(byte[] body)
    {
        var reason = body.Length > 2 ? body[2] : 0;
        var message = "";
        var at = 3;
        if (body.Length >= at + 2)
        {
            at += 2 + BitConverter.ToUInt16(body, at); // skip vp
            if (body.Length >= at + 2)
            {
                var length = BitConverter.ToUInt16(body, at);
                if (body.Length >= at + 2 + length)
                    message = Encoding.UTF8.GetString(body, at + 2, length).Trim('\0');
            }
        }
        return new AuthReply(AuthOutcome.Denied, reason, message);
    }
}
