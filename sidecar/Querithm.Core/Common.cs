using System.Security.Cryptography;

namespace Querithm.Core;

/// <summary>128-bit ULID: 48-bit ms timestamp + 80 random bits; 26-char Crockford base32 (§5 ids).</summary>
public readonly record struct Ulid(byte[] Bytes)
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static Ulid New()
    {
        var b = new byte[16];
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--) { b[i] = (byte)(ms & 0xFF); ms >>= 8; }
        RandomNumberGenerator.Fill(b.AsSpan(6));
        return new Ulid(b);
    }

    public override string ToString()
    {
        // 128 bits -> 26 chars, most significant first (first char carries 3 bits).
        Span<char> c = stackalloc char[26];
        var hi = (UInt128)0;
        foreach (var x in Bytes) hi = (hi << 8) | x;
        for (var i = 25; i >= 0; i--) { c[i] = Alphabet[(int)(hi & 31)]; hi >>= 5; }
        return new string(c);
    }

    public static Ulid Parse(string s)
    {
        if (s.Length != 26) throw new FormatException("ULID must be 26 characters");
        var v = (UInt128)0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = char.ToUpperInvariant(s[i]);
            var idx = Alphabet.IndexOf(ch);
            if (idx < 0) throw new FormatException($"Invalid ULID character '{s[i]}'");
            if (i == 0 && idx > 7) throw new FormatException("ULID overflows 128 bits");
            v = (v << 5) | (uint)idx;
        }
        var b = new byte[16];
        for (var i = 15; i >= 0; i--) { b[i] = (byte)(v & 0xFF); v >>= 8; }
        return new Ulid(b);
    }

    public static string NewId() => New().ToString();
}

/// <summary>Error surfaced to the webview as a JSON-RPC error with a stable code.</summary>
public sealed class QuerithmException(string code, string message, object? data = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? DataPayload { get; } = data;

    public static QuerithmException NotFound(string what, string id) => new("notFound", $"{what} '{id}' was not found.");
    public static QuerithmException Invalid(string message) => new("invalid", message);
}

public static class Time
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
