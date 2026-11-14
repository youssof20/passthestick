namespace PassTheStick.Protocol;

/// <summary>RFC 1982 style comparison for wrapping 16-bit sequence numbers.</summary>
public static class SerialNumber
{
    public static bool IsNewer(ushort candidate, ushort current)
    {
        var diff = (ushort)(candidate - current);
        return diff != 0 && diff < 0x8000;
    }
}
