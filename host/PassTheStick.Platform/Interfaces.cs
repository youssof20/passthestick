using PassTheStick.Protocol;

namespace PassTheStick.Platform;

/// <summary>A physical key as the OS sees it: set 1 scan code plus the extended (E0) flag.</summary>
public readonly record struct ScanKey(ushort ScanCode, bool Extended);

/// <summary>Injects keyboard input into the pinned game window. Windows implementation uses SendInput with scan codes.</summary>
public interface IInputInjector
{
    void KeyDown(ScanKey key);

    void KeyUp(ScanKey key);
}

/// <summary>A virtual controller per guest slot (ViGEmBus by default, VIIPER later).</summary>
public interface IVirtualPadBackend
{
    void Submit(int slot, in PadState state);

    /// <summary>Centers sticks and triggers and releases every button for the slot.</summary>
    void Neutralize(int slot);
}
