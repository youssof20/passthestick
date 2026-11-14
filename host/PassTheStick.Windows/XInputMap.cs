using Nefarius.ViGEm.Client.Targets.Xbox360;
using PassTheStick.Protocol;

namespace PassTheStick.Windows;

/// <summary>
/// Explicit button table. The legacy code looped over ViGEm button indexes and treated the XInput bit position
/// as the index, which does not line up for the face buttons. Every bit is named here instead.
/// </summary>
public static class XInputMap
{
    public static IReadOnlyList<(PadButtons Flag, Xbox360Button Button)> Buttons { get; } =
    [
        (PadButtons.DpadUp, Xbox360Button.Up),
        (PadButtons.DpadDown, Xbox360Button.Down),
        (PadButtons.DpadLeft, Xbox360Button.Left),
        (PadButtons.DpadRight, Xbox360Button.Right),
        (PadButtons.Start, Xbox360Button.Start),
        (PadButtons.Back, Xbox360Button.Back),
        (PadButtons.LeftThumb, Xbox360Button.LeftThumb),
        (PadButtons.RightThumb, Xbox360Button.RightThumb),
        (PadButtons.LeftShoulder, Xbox360Button.LeftShoulder),
        (PadButtons.RightShoulder, Xbox360Button.RightShoulder),
        (PadButtons.Guide, Xbox360Button.Guide),
        (PadButtons.A, Xbox360Button.A),
        (PadButtons.B, Xbox360Button.B),
        (PadButtons.X, Xbox360Button.X),
        (PadButtons.Y, Xbox360Button.Y),
    ];
}
