using Nefarius.Drivers.HidHide;

namespace PassTheStick.Host;

/// <summary>
/// Programmatic HidHide config: hide physical controller from game so only the ViGEm virtual device is visible.
/// Call from host when controller forwarding is enabled. Installer installs HidHide driver; this only configures it.
/// </summary>
public sealed class HidHideManager
{
    /// <summary>
    /// Hide a physical controller from all apps except ours.
    /// </summary>
    public static void HideDeviceForSession(string instanceId, string exePath)
    {
        var service = new HidHideControlService();
        service.AddBlockedInstanceId(instanceId);
        service.AddApplicationPath(exePath);
        service.IsActive = true;
    }

    /// <summary>
    /// Undo session hiding/whitelisting.
    /// </summary>
    public static void UnhideDeviceForSession(string instanceId, string exePath)
    {
        var service = new HidHideControlService();
        service.RemoveBlockedInstanceId(instanceId);
        service.RemoveApplicationPath(exePath);
    }
}
