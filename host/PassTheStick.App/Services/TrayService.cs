using PassTheStick.App.ViewModels;

namespace PassTheStick.App.Services;

/// <summary>The notification-area icon: quick actions while the game has the screen, and balloons for requests.</summary>
public sealed class TrayService : INotifier, IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _pass = new();
    private readonly System.Windows.Forms.ToolStripMenuItem _takeBack = new();
    private readonly System.Windows.Forms.ToolStripMenuItem _copy = new("Copy invite link");
    private readonly System.Windows.Forms.ToolStripMenuItem _end = new("End session");

    public TrayService(MainViewModel vm, Action showWindow, Action exit)
    {
        _pass.Text = $"Pass the stick ({vm.PassHotkeyText})";
        _takeBack.Text = $"Take the stick back ({vm.TakeBackHotkeyText})";
        _pass.Click += (_, _) => vm.PassNextCommand.Execute(null);
        _takeBack.Click += (_, _) => vm.TakeBackCommand.Execute(null);
        _copy.Click += (_, _) => vm.CopyInviteCommand.Execute(null);
        _end.Click += (_, _) => vm.EndCommand.Execute(null);

        var show = new System.Windows.Forms.ToolStripMenuItem("Open PassTheStick");
        show.Click += (_, _) => showWindow();
        var quit = new System.Windows.Forms.ToolStripMenuItem("Exit");
        quit.Click += (_, _) => exit();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.AddRange([show, new System.Windows.Forms.ToolStripSeparator(), _pass, _takeBack, _copy, _end, new System.Windows.Forms.ToolStripSeparator(), quit]);
        menu.Opening += (_, _) =>
        {
            _pass.Enabled = vm.IsLive;
            _takeBack.Enabled = vm.IsLive;
            _copy.Enabled = vm.HasRoom;
            _end.Enabled = vm.IsLive;
        };

        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/passthestick.ico"))!.Stream;
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "PassTheStick",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => showWindow();
    }

    public void Show(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _pass.Dispose();
        _takeBack.Dispose();
        _copy.Dispose();
        _end.Dispose();
    }
}
