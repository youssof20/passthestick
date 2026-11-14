namespace PassTheStick.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
    public const int MaxControlMessageBytes = 4096;
    public const int MaxSignalingMessageBytes = 8192;
    public static readonly TimeSpan StateTimeout = TimeSpan.FromMilliseconds(750);
}
