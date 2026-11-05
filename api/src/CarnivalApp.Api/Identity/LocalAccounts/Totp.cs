using System.Security.Cryptography;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// RFC 6238 with the authenticator-app defaults: HMAC-SHA1, 30-second steps, six digits. Identity's
/// own provider is not used because it accepts ±2 steps and does not say which step matched.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);
    private const int AllowedDrift = 1;

    public static long StepAt(DateTimeOffset at) => at.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    public static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// True with the matched step when <paramref name="code"/> is valid at <paramref name="now"/>
    /// give or take one step. Every candidate step is compared, in fixed time.
    /// </summary>
    public static bool Verify(string base32Key, string? code, DateTimeOffset now, out long matchedStep)
    {
        matchedStep = 0;
        var typed = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (typed.Length != Digits || !typed.All(char.IsAsciiDigit)) return false;
        if (DecodeBase32(base32Key) is not { Length: > 0 } key) return false;

        var typedBytes = System.Text.Encoding.ASCII.GetBytes(typed);
        var current = StepAt(now);
        var matched = false;
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    typedBytes, System.Text.Encoding.ASCII.GetBytes(Code(key, step))))
                continue;
            matchedStep = step;
            matched = true;
        }
        return matched;
    }

    public static string OtpauthUri(string issuer, string username, string base32Key)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(username);
        return $"otpauth://totp/{label}?secret={base32Key}&issuer={Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={(int)Step.TotalSeconds}";
    }

    public static byte[]? DecodeBase32(string? text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var raw in text ?? "")
        {
            if (raw is '=' || char.IsWhiteSpace(raw)) continue;
            var value = alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (value < 0) return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            output.Add((byte)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }
        return [.. output];
    }
}
