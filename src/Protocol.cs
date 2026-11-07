using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LanDoorbell;

/// <summary>
/// Wire protocol version 2. The shared secret never crosses the network.
///
///   receiver → sender   LANDOORBELL/2 &lt;receiverNonce&gt;
///   sender   → receiver RING &lt;senderNonce&gt; &lt;base64(senderName)&gt; &lt;mac&gt;     (or PING &lt;senderNonce&gt; &lt;mac&gt;)
///   receiver → sender   OK &lt;mac&gt; | PONG &lt;mac&gt; | DND &lt;mac&gt; | WAIT &lt;seconds&gt; &lt;mac&gt; | DENIED
///   receiver → sender   ACK &lt;base64(reply)&gt; &lt;mac&gt;      (later, if the person answers the alert)
///
/// Every mac is HMAC-SHA256 over the message kind, both nonces and the fields, keyed with
/// PBKDF2(secret). Fresh random nonces on both sides make captured messages useless for replay,
/// and the receiver's replies are signed too, so a fake receiver cannot forge "OK" or "On my way".
/// </summary>
internal static class Protocol
{
    public const string Greeting = "LANDOORBELL/2";
    public const int MaxLineBytes = 1024;
    public const int MaxNameChars = 40;
    public const int MaxReplyChars = 100;
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    /// <summary>How long a ring waits for the person at the receiver to answer the alert.</summary>
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromMinutes(2);

    private static readonly byte[] kdfSalt = Encoding.UTF8.GetBytes("LanDoorbellPro/2 shared-secret");

    /// <summary>Slow on purpose (~0.1 s) so a captured exchange cannot be brute-forced quickly.</summary>
    public static byte[] DeriveKey(string secret) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), kdfSalt, 150_000, HashAlgorithmName.SHA256, 32);

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static bool IsValidNonce(string nonce) =>
        nonce.Length is >= 16 and <= 64 && !nonce.Any(char.IsWhiteSpace);

    public static string Mac(byte[] key, params string[] parts) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Join("\n", parts))));

    public static bool MacMatches(byte[] key, string received, params string[] parts) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Mac(key, parts)),
            Encoding.ASCII.GetBytes(received));

    public static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    public static bool TryDecode(string base64, int maxChars, out string text)
    {
        text = "";
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (text.Length > maxChars) text = text[..maxChars];
            return text.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A random 24-character secret that is easy to copy between PCs.</summary>
    public static string GenerateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'k').Replace('/', 'Z');

    public static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct).ConfigureAwait(false);
}

/// <summary>
/// Reads '\n'-terminated lines with a hard size limit, so a peer cannot make us buffer
/// unlimited data. Honours the cancellation token (socket timeouts do not apply to async reads).
/// </summary>
internal sealed class LineReader
{
    private readonly Stream stream;
    private readonly byte[] buffer = new byte[Protocol.MaxLineBytes];
    private int count;

    public LineReader(Stream stream) => this.stream = stream;

    /// <returns>The line without its line ending, or null if the peer closed the connection.</returns>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            if (newline >= 0)
            {
                string line = Encoding.UTF8.GetString(buffer, 0, newline).TrimEnd('\r');
                count -= newline + 1;
                Buffer.BlockCopy(buffer, newline + 1, buffer, 0, count);
                return line;
            }
            if (count == buffer.Length)
                throw new InvalidDataException($"Line longer than {Protocol.MaxLineBytes} bytes.");

            int read = await stream.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) return null;
            count += read;
        }
    }
}
