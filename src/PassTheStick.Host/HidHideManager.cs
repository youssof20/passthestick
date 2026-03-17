using Nefarius.Drivers.HidHide;

namespace PassTheStick.Host;

/// <summary>
/// Programmatic HidHide config: hide physical controller from game so only the ViGEm virtual device is visible.
/// Call from host when controller forwarding is enabled. Installer installs HidHide driver; this only configures it.
/// </summary>
public sealed class HidHideManager
{
    /// <summary>Add the current process to the HidHide whitelist so it can still access HID devices if needed.</summary>
    public static void AddCurrentProcessToWhitelist()
    {
        try
        {
            using var service = HidHideControlService.GetInstance();
            var path = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(path))
                service.AddApplicationToWhitelist(path);
        }
        catch
        {
            // HidHide may not be installed; optional for keyboard-only use
        }
    }

    /// <summary>Add a device instance path to the block list so the game does not see it (only ViGEm remains visible).</summary>
    /// <param name="instancePath">Device instance path (e.g. from SetupAPI enumeration).</param>
    public static void AddDeviceToBlockList(string instancePath)
    {
        try
        {
            using var service = HidHideControlService.GetInstance();
            service.AddBlockListEntry(instancePath);
        }
        catch
        {
            // Driver not installed or path invalid
        }
    }

    /// <summary>Remove a device from the block list.</summary>
    public static void RemoveDeviceFromBlockList(string instancePath)
    {
        try
        {
            using var service = HidHideControlService.GetInstance();
            service.RemoveBlockListEntry(instancePath);
        }
        catch { }
    }
}
