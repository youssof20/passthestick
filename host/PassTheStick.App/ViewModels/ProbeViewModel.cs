using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PassTheStick.App.Services;
using PassTheStick.Protocol;

namespace PassTheStick.App.ViewModels;

public sealed partial class ButtonChip : ObservableObject
{
    public ButtonChip(string name, PadButtons flag)
    {
        Name = name;
        Flag = flag;
    }

    public string Name { get; }

    public PadButtons Flag { get; }

    [ObservableProperty]
    private bool _isDown;
}

/// <summary>The host's live picture of one guest's controls while that guest runs the input test.</summary>
public sealed partial class ProbeViewModel : ObservableObject
{
    public ProbeViewModel()
    {
        foreach (var (name, flag) in new (string, PadButtons)[]
        {
            ("A", PadButtons.A), ("B", PadButtons.B), ("X", PadButtons.X), ("Y", PadButtons.Y),
            ("LB", PadButtons.LeftShoulder), ("RB", PadButtons.RightShoulder), ("Back", PadButtons.Back), ("Start", PadButtons.Start),
            ("Up", PadButtons.DpadUp), ("Down", PadButtons.DpadDown), ("Left", PadButtons.DpadLeft), ("Right", PadButtons.DpadRight),
            ("LS", PadButtons.LeftThumb), ("RS", PadButtons.RightThumb),
        })
        {
            Buttons.Add(new ButtonChip(name, flag));
        }
    }

    public ObservableCollection<ButtonChip> Buttons { get; } = [];

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Axis meters run from 0 (full negative) to 1 (full positive); centered is 0.5.</summary>
    [ObservableProperty]
    private double _leftX = 0.5;

    [ObservableProperty]
    private double _leftY = 0.5;

    [ObservableProperty]
    private double _rightX = 0.5;

    [ObservableProperty]
    private double _rightY = 0.5;

    [ObservableProperty]
    private double _leftTrigger;

    [ObservableProperty]
    private double _rightTrigger;

    [ObservableProperty]
    private string _keysText = "No keys down";

    public void Apply(ProbeInfo info)
    {
        IsActive = info.Active;
        Title = info.Active ? $"Testing {info.GuestName}'s controls" : string.Empty;
        foreach (var chip in Buttons)
        {
            chip.IsDown = info.Active && (info.Pad.Buttons & chip.Flag) != 0;
        }

        LeftX = Norm(info.Active ? info.Pad.Lx : (short)0);
        LeftY = Norm(info.Active ? info.Pad.Ly : (short)0);
        RightX = Norm(info.Active ? info.Pad.Rx : (short)0);
        RightY = Norm(info.Active ? info.Pad.Ry : (short)0);
        LeftTrigger = info.Active ? info.Pad.Lt / 255.0 : 0;
        RightTrigger = info.Active ? info.Pad.Rt / 255.0 : 0;
        KeysText = info.Active && info.Keys.Count > 0 ? "Keys down: " + string.Join(", ", info.Keys.Order(StringComparer.Ordinal)) : "No keys down";
    }

    private static double Norm(short axis) => (axis + 32768.0) / 65535.0;
}
