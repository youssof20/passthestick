using System.Security.Cryptography;

namespace PassTheStick.Protocol;

/// <summary>Six-character room codes with no 0, O, 1, I or L, drawn from a CSPRNG.</summary>
public static class RoomCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    public static bool IsValid(string? code)
    {
        if (code is null || code.Length != Length)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Uppercases and trims what a person typed; returns null when it is not a valid code.</summary>
    public static string? Normalize(string? typed)
    {
        var code = typed?.Trim().ToUpperInvariant();
        return IsValid(code) ? code : null;
    }
}
