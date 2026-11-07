using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LanDoorbell;

/// <summary>A verified ring. Complete <see cref="Reply"/> with a message ("On my way") or null.</summary>
internal sealed record IncomingRing(string SenderName, IPAddress RemoteAddress, DateTime Time, TaskCompletionSource<string?> Reply);

/// <summary>
/// Listens for rings on IPv4 and IPv6. Protects itself with: handshake timeouts, a line-size limit,
/// a cap on simultaneous connections, a per-PC cooldown, and a lock-out after repeated wrong secrets.
/// Events are raised on thread-pool threads.
/// </summary>
internal sealed class RingServer : IDisposable
{
    private const int MaxConcurrentHandshakes = 16;
    private const int MaxFailuresPerMinute = 10;

    private readonly int port;
    private readonly byte[] key;
    private readonly TimeSpan cooldown;
    private readonly Func<bool> isDoNotDisturb;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim handshakeSlots = new(MaxConcurrentHandshakes);
    private readonly ConcurrentDictionary<IPAddress, DateTime> lastRing = new();
    private readonly ConcurrentDictionary<IPAddress, Queue<DateTime>> failures = new();
    private TcpListener? listener;

    public event Action<IncomingRing>? RingReceived;
    /// <summary>A ring with the right secret that was refused (sender, address, reason).</summary>
    public event Action<string, IPAddress, string>? RingBlocked;

    public RingServer(int port, byte[] key, int cooldownSeconds, Func<bool> isDoNotDisturb)
    {
        this.port = port;
        this.key = key;
        cooldown = TimeSpan.FromSeconds(Math.Max(0, cooldownSeconds));
        this.isDoNotDisturb = isDoNotDisturb;
    }

    public int Port => port;

    public void Start()
    {
        listener = CreateListener();
        _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
    }

    private TcpListener CreateListener()
    {
        if (Socket.OSSupportsIPv6)
        {
            var dual = new TcpListener(IPAddress.IPv6Any, port);
            try
            {
                dual.Server.DualMode = true;   // accept IPv4 and IPv6 on one socket
                dual.Start();
                return dual;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                dual.Stop();
                Log.Warn("IPv6 is not available; listening on IPv4 only", ex);
            }
        }
        var v4 = new TcpListener(IPAddress.Any, port);
        v4.Start();
        return v4;
    }

    private async Task AcceptLoopAsync(TcpListener tcp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                Log.Warn("Accept failed", ex);
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }

            if (!handshakeSlots.Wait(0))
            {
                Log.Warn("Too many connections at once; dropping one");
                client.Dispose();
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        int slotHeld = 1;
        void ReleaseSlot()
        {
            if (Interlocked.Exchange(ref slotHeld, 0) == 1) handshakeSlots.Release();
        }

        IPAddress remote = IPAddress.None;
        try
        {
            using (client)
            {
                remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
                if (IsLockedOut(remote)) return;

                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(Protocol.HandshakeTimeout);
                var stream = client.GetStream();
                var reader = new LineReader(stream);

                string serverNonce = Protocol.NewNonce();
                await Protocol.WriteLineAsync(stream, $"{Protocol.Greeting} {serverNonce}", handshake.Token).ConfigureAwait(false);
                string? line = await reader.ReadLineAsync(handshake.Token).ConfigureAwait(false);
                if (line == null) return;

                string[] parts = line.Split(' ');
                if (parts[0].StartsWith("RING:", StringComparison.Ordinal))
                {
                    Log.Warn($"{remote} uses the old version 1 protocol. Update LAN Doorbell Pro on that PC.");
                    await Protocol.WriteLineAsync(stream, "DENIED", handshake.Token).ConfigureAwait(false);
                    return;
                }
                if (parts.Length < 3 || !Protocol.IsValidNonce(parts[1]))
                {
                    await DenyAsync(stream, remote, "malformed message", handshake.Token).ConfigureAwait(false);
                    return;
                }

                string clientNonce = parts[1];
                if (parts[0] == "PING" && parts.Length == 3)
                {
                    if (!Protocol.MacMatches(key, parts[2], "PING", serverNonce, clientNonce))
                    {
                        await DenyAsync(stream, remote, "connection test with wrong secret", handshake.Token).ConfigureAwait(false);
                        return;
                    }
                    await Protocol.WriteLineAsync(stream, "PONG " + Protocol.Mac(key, "PONG", serverNonce, clientNonce), handshake.Token).ConfigureAwait(false);
                    Log.Info($"Connection test from {remote} OK");
                    return;
                }

                if (parts[0] != "RING" || parts.Length != 4 ||
                    !Protocol.MacMatches(key, parts[3], "RING", serverNonce, clientNonce, parts[2]))
                {
                    await DenyAsync(stream, remote, "ring with wrong secret", handshake.Token).ConfigureAwait(false);
                    return;
                }

                if (!Protocol.TryDecode(parts[2], Protocol.MaxNameChars, out string senderName))
                    senderName = remote.ToString();

                if (isDoNotDisturb())
                {
                    await Protocol.WriteLineAsync(stream, "DND " + Protocol.Mac(key, "DND", serverNonce, clientNonce), handshake.Token).ConfigureAwait(false);
                    RingBlocked?.Invoke(senderName, remote, "Blocked: do not disturb");
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (lastRing.TryGetValue(remote, out var previous) && now - previous < cooldown)
                {
                    string wait = Math.Ceiling((cooldown - (now - previous)).TotalSeconds).ToString();
                    await Protocol.WriteLineAsync(stream, $"WAIT {wait} " + Protocol.Mac(key, "WAIT", serverNonce, clientNonce, wait), handshake.Token).ConfigureAwait(false);
                    RingBlocked?.Invoke(senderName, remote, "Ignored: rang again too quickly");
                    return;
                }
                lastRing[remote] = now;

                await Protocol.WriteLineAsync(stream, "OK " + Protocol.Mac(key, "OK", serverNonce, clientNonce), handshake.Token).ConfigureAwait(false);
                Log.Info($"Ring from {senderName} ({remote})");

                // Keep the connection open so the person here can answer ("On my way").
                // Free the handshake slot first: waiting replies must not block new rings.
                ReleaseSlot();
                var ring = new IncomingRing(senderName, remote, DateTime.Now,
                    new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
                RingReceived?.Invoke(ring);

                string? reply;
                try { reply = await ring.Reply.Task.WaitAsync(Protocol.ReplyWindow, ct).ConfigureAwait(false); }
                catch (TimeoutException) { reply = null; }
                ring.Reply.TrySetResult(null);

                if (!string.IsNullOrWhiteSpace(reply))
                {
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    send.CancelAfter(TimeSpan.FromSeconds(3));
                    string encoded = Protocol.Encode(reply.Length > Protocol.MaxReplyChars ? reply[..Protocol.MaxReplyChars] : reply);
                    await Protocol.WriteLineAsync(stream, $"ACK {encoded} " + Protocol.Mac(key, "ACK", serverNonce, clientNonce, encoded), send.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"Connection from {remote} timed out");
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or ObjectDisposedException)
        {
            Log.Warn($"Connection from {remote} failed", ex);
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected error handling connection from {remote}", ex);
        }
        finally
        {
            ReleaseSlot();
        }
    }

    private async Task DenyAsync(NetworkStream stream, IPAddress remote, string reason, CancellationToken ct)
    {
        RecordFailure(remote);
        Log.Warn($"Denied {remote}: {reason}");
        await Protocol.WriteLineAsync(stream, "DENIED", ct).ConfigureAwait(false);
    }

    private void RecordFailure(IPAddress remote)
    {
        var queue = failures.GetOrAdd(remote, _ => new Queue<DateTime>());
        lock (queue) queue.Enqueue(DateTime.UtcNow);
    }

    /// <summary>Too many wrong secrets from one address in the last minute: ignore it silently.</summary>
    private bool IsLockedOut(IPAddress remote)
    {
        if (!failures.TryGetValue(remote, out var queue)) return false;
        lock (queue)
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
            return queue.Count >= MaxFailuresPerMinute;
        }
    }

    public void Dispose()
    {
        // The token source is cancelled but not disposed: connection handlers may still be using its token.
        cts.Cancel();
        try { listener?.Stop(); } catch (SocketException) { }
        listener = null;
    }
}
