using Nefarius.Drivers.HidHide;

namespace PassTheStick.Host;

/// <summary>
/// Programmatic HidHide: hide physical gamepads from other apps while whitelisting PassTheStick (see ViGEm path).
/// </summary>
public static class HidHideManager
{
    public static void TryBeginPassthroughSession(string exePath, IReadOnlyList<string> instanceIdsToBlock, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(exePath) || instanceIdsToBlock.Count == 0)
        {
            log?.Invoke("[controller] HidHide skipped (no devices to hide)");
            return;
        }

        try
        {
            var service = new HidHideControlService();
            service.AddApplicationPath(exePath);
            foreach (var id in instanceIdsToBlock)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                service.AddBlockedInstanceId(id.Trim());
            }

            service.IsActive = true;
            log?.Invoke($"[controller] HidHide activated ({instanceIdsToBlock.Count} device(s))");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[controller] HidHide not available: {ex.Message} (double input may occur)");
        }
    }

    public static void TryEndPassthroughSession(string exePath, IReadOnlyList<string> instanceIdsBlocked, Action<string>? log)
    {
        try
        {
            var service = new HidHideControlService();
            foreach (var id in instanceIdsBlocked)
            {
                try { service.RemoveBlockedInstanceId(id); } catch { /* ignore */ }
            }

            try { service.RemoveApplicationPath(exePath); } catch { /* ignore */ }

            try { service.IsActive = false; } catch { /* ignore */ }

            log?.Invoke("[controller] HidHide deactivated");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[controller] HidHide cleanup: {ex.Message}");
        }
    }

    /// <summary>Legacy single-device API retained for callers that already know an instance id.</summary>
    public static void HideDeviceForSession(string instanceId, string exePath) =>
        TryBeginPassthroughSession(exePath, new[] { instanceId }, _ => { });

    /// <summary>Legacy cleanup for a single instance id.</summary>
    public static void UnhideDeviceForSession(string instanceId, string exePath) =>
        TryEndPassthroughSession(exePath, new[] { instanceId }, _ => { });
}
