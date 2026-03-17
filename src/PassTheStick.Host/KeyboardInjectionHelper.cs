using System.Runtime.InteropServices;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>
/// Maps guest VK to host scan code and injects. Use MapVirtualKey so AZERTY/QWERTY works.
/// </summary>
public static class KeyboardInjectionHelper
{
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>Inject a KEY_EVENT from the relay. Uses host layout (MapVirtualKey) not guest scan code.</summary>
    public static void InjectKeyEvent(KeyEventMessage msg)
    {
        uint scanCode = MapVirtualKey((uint)msg.Vk, MAPVK_VK_TO_VSC);
        if (scanCode == 0)
            scanCode = (uint)msg.Sc; // fallback if mapping fails
        InputInjector.InjectKey((ushort)scanCode, msg.Down);
    }
}
