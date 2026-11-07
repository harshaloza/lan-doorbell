using System.Net;
using System.Net.Sockets;

namespace LanDoorbell;

internal enum RingStatus { Ok, DoNotDisturb, Cooldown, Denied, Error }

/// <param name="Reply">For a successful ring: completes with the receiver's answer ("On my way"), or null.</param>
internal sealed record RingResult(ReceiverEntry Target, RingStatus Status, string Detail, Task<string?>? Reply = null)
{
    public bool Delivered => Status == RingStatus.Ok;
    public bool IsFailure => Status is RingStatus.Denied or RingStatus.Error;
}

/// <summary>Sends rings and connection tests to receivers (protocol: see <see cref="Protocol"/>).</summary>
internal static class RingClient
{
    private static readonly TimeSpan PerAddressConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GreetingTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(10);

    public static Task<RingResult> RingAsync(ReceiverEntry target, int port, byte[] key, string senderName, CancellationToken ct = default) =>
        ExchangeAsync(target, port, key, senderName, isTest: false, ct);

    /// <summary>Checks that the receiver is reachable and uses the same secret. Does not ring.</summary>
    public static Task<RingResult> TestAsync(ReceiverEntry target, int port, byte[] key, CancellationToken ct = default) =>
        ExchangeAsync(target, port, key, "", isTest: true, ct);

    private static async Task<RingResult> ExchangeAsync(ReceiverEntry target, int port, byte[] key, string senderName, bool isTest, CancellationToken ct)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(OverallTimeout);
        TcpClient? client = null;
        bool handedOff = false;
        string stage = "connect";
        try
        {
            client = await ConnectAsync(target.Host, port, overall.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var reader = new LineReader(stream);

            stage = "greeting";
            string? greeting;
            using (var greetingCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
            {
                greetingCts.CancelAfter(GreetingTimeout);
                greeting = await reader.ReadLineAsync(greetingCts.Token).ConfigureAwait(false);
            }
            if (greeting == null)
                return new(target, RingStatus.Error,
                    "the receiver closed the connection — after several wrong secrets it waits a minute before answering again");
            string[] g = greeting.Split(' ');
            if (g.Length != 2 || g[0] != Protocol.Greeting || !Protocol.IsValidNonce(g[1]))
                return new(target, RingStatus.Error, "unexpected answer — is that PC running LAN Doorbell Pro 2?");

            stage = "reply";
            string serverNonce = g[1];
            string clientNonce = Protocol.NewNonce();
            if (isTest)
            {
                await Protocol.WriteLineAsync(stream, $"PING {clientNonce} " + Protocol.Mac(key, "PING", serverNonce, clientNonce), overall.Token).ConfigureAwait(false);
            }
            else
            {
                string name = Protocol.Encode(senderName.Length > Protocol.MaxNameChars ? senderName[..Protocol.MaxNameChars] : senderName);
                await Protocol.WriteLineAsync(stream, $"RING {clientNonce} {name} " + Protocol.Mac(key, "RING", serverNonce, clientNonce, name), overall.Token).ConfigureAwait(false);
            }

            string? response = await reader.ReadLineAsync(overall.Token).ConfigureAwait(false);
            string[] r = response?.Split(' ') ?? Array.Empty<string>();
            bool Signed(string kind, params string[] extra) =>
                r.Length == 2 + extra.Length &&
                Protocol.MacMatches(key, r[^1], new[] { kind, serverNonce, clientNonce }.Concat(extra).ToArray());

            switch (r.FirstOrDefault())
            {
                case "PONG" when isTest && Signed("PONG"):
                    return new(target, RingStatus.Ok, "reachable, secret matches");
                case "OK" when !isTest && Signed("OK"):
                    handedOff = true;
                    return new(target, RingStatus.Ok, "rang", WaitForReplyAsync(client, reader, key, serverNonce, clientNonce));
                case "DND" when Signed("DND"):
                    return new(target, RingStatus.DoNotDisturb, "do not disturb is on");
                case "WAIT" when r.Length == 3 && Signed("WAIT", r[1]):
                    return new(target, RingStatus.Cooldown, $"you rang moments ago — try again in {r[1]} s");
                case "DENIED":
                    return new(target, RingStatus.Denied, "the shared secret does not match (or that PC runs an old version)");
                case null:
                    return new(target, RingStatus.Error, "the receiver closed the connection");
                default:
                    return new(target, RingStatus.Error, "reply was not signed with the same secret");
            }
        }
        catch (Exception ex)
        {
            if (stage == "greeting" && ex is OperationCanceledException && !ct.IsCancellationRequested)
                return new(target, RingStatus.Error, "connected, but no greeting — that PC may run an old version of LAN Doorbell");
            return new(target, RingStatus.Error, Describe(ex));
        }
        finally
        {
            if (!handedOff) client?.Dispose();
        }
    }

    private static async Task<string?> WaitForReplyAsync(TcpClient client, LineReader reader, byte[] key, string serverNonce, string clientNonce)
    {
        using (client)
        {
            try
            {
                using var cts = new CancellationTokenSource(Protocol.ReplyWindow + TimeSpan.FromSeconds(15));
                string? line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                string[] p = line?.Split(' ') ?? Array.Empty<string>();
                if (p.Length == 3 && p[0] == "ACK" &&
                    Protocol.MacMatches(key, p[2], "ACK", serverNonce, clientNonce, p[1]) &&
                    Protocol.TryDecode(p[1], Protocol.MaxReplyChars, out string text))
                    return text;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or InvalidDataException)
            {
                // No answer from the person at the receiver: that is normal.
            }
            return null;
        }
    }

    /// <summary>Tries IPv4 addresses first (most reliable on home/office LANs), then IPv6.</summary>
    private static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        Exception? last = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var client = new TcpClient(address.AddressFamily);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(PerAddressConnectTimeout);
            try
            {
                await client.ConnectAsync(address, port, attempt.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                client.Dispose();
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    /// <summary>Plain-language reason for a connection failure.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } =>
            "computer name not found on the network — try its IP address",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            "connection refused — LAN Doorbell is not running there, or its receiver is off",
        SocketException { SocketErrorCode: SocketError.TimedOut } or OperationCanceledException =>
            "no answer — the PC is off, the address is wrong, or a firewall is blocking the port",
        SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
            "that address cannot be reached from this network",
        SocketException s => $"network error ({s.SocketErrorCode})",
        IOException => "the connection was dropped",
        _ => ex.Message
    };
}
