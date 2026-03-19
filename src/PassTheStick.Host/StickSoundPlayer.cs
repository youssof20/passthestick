using System.Media;

namespace PassTheStick.Host;

/// <summary>Opt-in stick sounds (Windows system sounds — no bundled WAV required).</summary>
public static class StickSoundPlayer
{
    public static void PlayPass()
    {
        try { SystemSounds.Hand.Play(); } catch { }
    }

    public static void PlayReceive()
    {
        try { SystemSounds.Asterisk.Play(); } catch { }
    }
}
