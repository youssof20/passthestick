using Nefarius.Drivers.HidHide;

namespace PassTheStick.Windows;

public sealed class NefariusHidHideService : IHidHideService
{
    private readonly HidHideControlService _service = new();

    public bool IsActive
    {
        get => _service.IsActive;
        set => _service.IsActive = value;
    }

    public IReadOnlyList<string> BlockedInstanceIds => [.. _service.BlockedInstanceIds];

    public IReadOnlyList<string> ApplicationPaths => [.. _service.ApplicationPaths];

    public void AddBlockedInstanceId(string instanceId) => _service.AddBlockedInstanceId(instanceId);

    public void RemoveBlockedInstanceId(string instanceId) => _service.RemoveBlockedInstanceId(instanceId);

    public void AddApplicationPath(string path) => _service.AddApplicationPath(path);

    public void RemoveApplicationPath(string path) => _service.RemoveApplicationPath(path);
}
