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

    public static ushort MapToHostScanCode(int vk, int guestScanCode)
    {
        uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        if (scanCode == 0)
        {
            scanCode = (uint)guestScanCode; // fallback if mapping fails
            InputDebugLog.Log(
                $"MapVirtualKey(vk={vk}) returned 0; using guest scan code sc={guestScanCode} \u2192 host sc={scanCode}");
        }
        else
        {
            InputDebugLog.Log($"Mapping vk={vk} guestSc={guestScanCode} \u2192 host scan code={scanCode}");
        }

        return (ushort)scanCode;
    }

    public static void InjectScanCode(ushort scanCode, bool down)
    {
        InputInjector.InjectKey(scanCode, down);
    }
}
