using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using PassTheStick.Platform;
using PassTheStick.Protocol;
using PassTheStick.Windows;

namespace PassTheStick.Windows.Tests;

public class NativeLayoutTests
{
    [Fact]
    public void Input_struct_matches_the_win32_x64_layout_that_SendInput_requires()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.Input>());
        Assert.Equal(8, (int)Marshal.OffsetOf<NativeMethods.Input>(nameof(NativeMethods.Input.Keyboard)));
        Assert.Equal(24, Marshal.SizeOf<NativeMethods.KeybdInput>());
        Assert.Equal(32, Marshal.SizeOf<NativeMethods.MouseInput>());
    }

    [Theory]
    [InlineData(0x11, false, true, 0x0008u)]
    [InlineData(0x11, false, false, 0x000Au)]
    [InlineData(0x4B, true, true, 0x0009u)]
    [InlineData(0x4B, true, false, 0x000Bu)]
    public void Key_flags_use_scan_codes_and_set_extended_and_key_up_correctly(int scan, bool extended, bool down, uint expected) =>
        Assert.Equal(expected, SendInputInjector.Flags(new ScanKey((ushort)scan, extended), down));
}

public class XInputMapTests
{
    [Fact]
    public void Every_pad_button_flag_is_mapped_exactly_once()
    {
        var all = Enum.GetValues<PadButtons>().Where(f => f != PadButtons.None).ToHashSet();
        var mapped = XInputMap.Buttons.Select(b => b.Flag).ToList();
        Assert.Equal(all.Count, mapped.Count);
        Assert.Equal(all, mapped.ToHashSet());
        Assert.Equal(mapped.Count, XInputMap.Buttons.Select(b => b.Button).Distinct().Count());
    }

    [Theory]
    [InlineData(PadButtons.A, "A")]
    [InlineData(PadButtons.B, "B")]
    [InlineData(PadButtons.X, "X")]
    [InlineData(PadButtons.Y, "Y")]
    public void Face_buttons_map_to_the_button_with_the_same_name(PadButtons flag, string name)
    {
        var button = XInputMap.Buttons.Single(b => b.Flag == flag).Button;
        var member = (object?)typeof(Xbox360Button).GetField(name)?.GetValue(null) ?? typeof(Xbox360Button).GetProperty(name)?.GetValue(null);
        Assert.Same(member, button);
    }
}

public class ForegroundPolicyTests
{
    private static nint NoOwner(nint w) => w;

    [Fact]
    public void Matches_the_pinned_window_and_windows_it_owns_but_not_other_windows()
    {
        Assert.True(ForegroundPolicy.Matches(10, 10, NoOwner));
        Assert.True(ForegroundPolicy.Matches(11, 10, w => w == 11 ? 10 : w));
        Assert.False(ForegroundPolicy.Matches(12, 10, NoOwner));
    }

    [Fact]
    public void Nothing_pinned_or_nothing_in_front_never_matches()
    {
        Assert.False(ForegroundPolicy.Matches(0, 10, NoOwner));
        Assert.False(ForegroundPolicy.Matches(10, 0, NoOwner));
        Assert.False(ForegroundPolicy.Matches(0, 0, NoOwner));
    }
}

internal sealed class FakeHidHide : IHidHideService
{
    public bool IsActive { get; set; }

    public List<string> Blocked { get; } = [];

    public List<string> Apps { get; } = [];

    public Func<string, bool>? FailAdd { get; set; }

    public bool FailRemove { get; set; }

    public IReadOnlyList<string> BlockedInstanceIds => [.. Blocked];

    public IReadOnlyList<string> ApplicationPaths => [.. Apps];

    public void AddBlockedInstanceId(string instanceId)
    {
        if (FailAdd?.Invoke(instanceId) == true)
        {
            throw new InvalidOperationException("driver error");
        }

        Blocked.Add(instanceId);
    }

    public void RemoveBlockedInstanceId(string instanceId)
    {
        if (FailRemove)
        {
            throw new InvalidOperationException("driver error");
        }

        Blocked.Remove(instanceId);
    }

    public void AddApplicationPath(string path) => Apps.Add(path);

    public void RemoveApplicationPath(string path) => Apps.Remove(path);
}

public sealed class HidHideGuardTests : IDisposable
{
    private const string Exe = @"C:\Program Files\PassTheStick\PassTheStick.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pts-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHidHide _hid = new();
    private readonly string _journal;

    public HidHideGuardTests() => _journal = Path.Combine(_dir, "hidhide.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private HidHideGuard Guard() => new(_hid, _journal, NullLogger.Instance);

    [Fact]
    public void Begin_hides_devices_activates_and_Restore_puts_everything_back()
    {
        var g = Guard();
        g.Begin(Exe, ["HID\\A", "HID\\B"]);
        Assert.Equal(["HID\\A", "HID\\B"], _hid.Blocked);
        Assert.Equal([Exe], _hid.Apps);
        Assert.True(_hid.IsActive);
        Assert.True(File.Exists(_journal));

        Assert.True(g.Restore());
        Assert.Empty(_hid.Blocked);
        Assert.Empty(_hid.Apps);
        Assert.False(_hid.IsActive);
        Assert.False(File.Exists(_journal));
    }

    [Fact]
    public void Never_removes_entries_the_user_already_had_and_keeps_their_active_flag()
    {
        _hid.Blocked.Add("HID\\MINE");
        _hid.Apps.Add(Exe);
        _hid.IsActive = true;
        var g = Guard();
        g.Begin(Exe, ["HID\\MINE", "HID\\NEW"]);
        g.Restore();
        Assert.Equal(["HID\\MINE"], _hid.Blocked);
        Assert.Equal([Exe], _hid.Apps);
        Assert.True(_hid.IsActive);
    }

    [Fact]
    public void A_crash_leaves_a_journal_and_the_next_start_undoes_it()
    {
        Guard().Begin(Exe, ["HID\\A"]);
        Assert.True(_hid.IsActive);

        var afterRestart = Guard();
        Assert.True(afterRestart.RecoverLeftovers());
        Assert.Empty(_hid.Blocked);
        Assert.False(_hid.IsActive);
    }

    [Fact]
    public void A_failure_halfway_through_Begin_is_still_undone_from_the_journal()
    {
        _hid.FailAdd = id => id == "HID\\B";
        Assert.Throws<InvalidOperationException>(() => Guard().Begin(Exe, ["HID\\A", "HID\\B"]));
        _hid.FailAdd = null;
        Guard().RecoverLeftovers();
        Assert.Empty(_hid.Blocked);
        Assert.Empty(_hid.Apps);
        Assert.False(_hid.IsActive);
    }

    [Fact]
    public void Failed_undo_keeps_the_journal_so_the_next_start_retries()
    {
        var g = Guard();
        g.Begin(Exe, ["HID\\A"]);
        _hid.FailRemove = true;
        g.Restore();
        Assert.True(File.Exists(_journal));
        _hid.FailRemove = false;
        g.Restore();
        Assert.False(File.Exists(_journal));
        Assert.Empty(_hid.Blocked);
    }

    [Fact]
    public void A_corrupt_journal_is_discarded_without_touching_hidhide()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_journal, "{not json");
        _hid.Blocked.Add("HID\\MINE");
        Assert.False(Guard().RecoverLeftovers());
        Assert.False(File.Exists(_journal));
        Assert.Equal(["HID\\MINE"], _hid.Blocked);
    }

    [Fact]
    public void Restore_with_no_journal_does_nothing_and_Begin_ignores_blank_ids()
    {
        var g = Guard();
        Assert.False(g.Restore());
        g.Begin(Exe, ["", "  ", "HID\\A"]);
        Assert.Equal(["HID\\A"], _hid.Blocked);
    }

    [Fact]
    public void Dispose_restores()
    {
        var g = Guard();
        g.Begin(Exe, ["HID\\A"]);
        g.Dispose();
        Assert.Empty(_hid.Blocked);
    }

    [Fact]
    public void The_journal_is_valid_json_with_the_documented_fields()
    {
        Guard().Begin(Exe, ["HID\\A"]);
        using var doc = JsonDocument.Parse(File.ReadAllText(_journal));
        Assert.Equal(1, doc.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("HID\\A", doc.RootElement.GetProperty("AddedInstanceIds")[0].GetString());
        Assert.False(doc.RootElement.GetProperty("WasActive").GetBoolean());
    }
}
