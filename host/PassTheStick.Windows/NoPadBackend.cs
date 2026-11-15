using PassTheStick.Platform;
using PassTheStick.Protocol;

namespace PassTheStick.Windows;

/// <summary>Used when no virtual-pad driver is available. Guests can still send keyboard input.</summary>
public sealed class NoPadBackend : IVirtualPadBackend
{
    public void Submit(int slot, in PadState state)
    {
    }

    public void Neutralize(int slot)
    {
    }
}
