using System.Management;

namespace PassTheStick.Host;

/// <summary>
/// Best-effort WMI enumeration of HID devices that look like physical gamepads (excludes ViGEm / obvious virtual strings).
/// </summary>
public static class HidPhysicalGamepadEnumerator
{
    public static List<string> EnumerateCandidateInstanceIds()
    {
        var list = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID, Name FROM Win32_PnPEntity WHERE PNPClass='HIDClass'");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString() ?? string.Empty;
                var id = mo["PNPDeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                var n = name.ToLowerInvariant();
                if (n.Contains("vigem", StringComparison.Ordinal) ||
                    n.Contains("nefarius", StringComparison.Ordinal) ||
                    n.Contains("virtual", StringComparison.Ordinal))
                    continue;

                if (n.Contains("controller", StringComparison.Ordinal) ||
                    n.Contains("gamepad", StringComparison.Ordinal) ||
                    n.Contains("joystick", StringComparison.Ordinal) ||
                    n.Contains("xbox", StringComparison.Ordinal) ||
                    n.Contains("dualshock", StringComparison.Ordinal) ||
                    n.Contains("dualsense", StringComparison.Ordinal))
                {
                    list.Add(id);
                }
            }
        }
        catch
        {
            // WMI unavailable or denied — list stays empty; caller can continue without HidHide.
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
