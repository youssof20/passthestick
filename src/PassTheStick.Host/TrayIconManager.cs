using System.IO;
using System.Windows;
using PassTheStick.Shared;
using WinForms = System.Windows.Forms;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace PassTheStick.Host;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly SessionManager _session;
    private readonly Func<IReadOnlyList<PlayerInfo>> _getPlayers;
    private readonly Action<PlayerInfo> _passStickTo;
    private readonly Action _pinGame;
    private readonly Action _soloTest;
    private readonly Func<bool> _isRelayRunning;
    private readonly Action _startRelay;
    private readonly Action _stopRelay;

    private ToolStripMenuItem? _playersHeader;
    private ToolStripMenuItem? _relayItem;

    public TrayIconManager(
        SessionManager session,
        Func<IReadOnlyList<PlayerInfo>> getPlayers,
        Action<PlayerInfo> passStickTo,
        Action pinGame,
        Action soloTest,
        Func<bool> isRelayRunning,
        Action startRelay,
        Action stopRelay)
    {
        _session = session;
        _getPlayers = getPlayers;
        _passStickTo = passStickTo;
        _pinGame = pinGame;
        _soloTest = soloTest;
        _isRelayRunning = isRelayRunning;
        _startRelay = startRelay;
        _stopRelay = stopRelay;

        var iconInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/passthestick.ico"));
        if (iconInfo == null)
            throw new InvalidOperationException("Missing embedded resource: passthestick.ico");

        using var iconStream = iconInfo.Stream;
        var trayIcon = new System.Drawing.Icon(iconStream);

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "PassTheStick",
            Icon = trayIcon
        };

        _notifyIcon.ContextMenuStrip = BuildMenu();
        _notifyIcon.DoubleClick += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => _pinGame());
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("Pin current window as game", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(_pinGame)));
        menu.Items.Add(new ToolStripSeparator());

        _playersHeader = new ToolStripMenuItem("Pass stick to:")
        {
            Enabled = false
        };
        menu.Items.Add(_playersHeader);

        menu.Opening += (_, _) => RefreshPlayers(menu);

        menu.Items.Add(new ToolStripSeparator());

        _relayItem = new ToolStripMenuItem("Start relay server", null, (_, _) =>
        {
            if (_isRelayRunning()) _stopRelay(); else _startRelay();
        });
        menu.Items.Add(_relayItem);

        menu.Items.Add(new ToolStripMenuItem("Solo test mode", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(_soloTest)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown())));

        return menu;
    }

    private void RefreshPlayers(ContextMenuStrip menu)
    {
        if (_relayItem != null)
            _relayItem.Text = _isRelayRunning() ? "Relay running (stop)" : "Start relay server";

        // Remove old player entries (items between header and next separator)
        var header = _playersHeader;
        if (header == null) return;
        int headerIndex = menu.Items.IndexOf(header);
        if (headerIndex < 0) return;

        int i = headerIndex + 1;
        while (i < menu.Items.Count && menu.Items[i] is ToolStripMenuItem mi && mi.Tag as string == "player")
            menu.Items.RemoveAt(i);

        var players = _getPlayers();
        foreach (var p in players)
        {
            var item = new ToolStripMenuItem(p.Name, null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => _passStickTo(p)))
            {
                Tag = "player",
                Enabled = true
            };
            menu.Items.Insert(i++, item);
        }

        if (players.Count == 0)
        {
            var none = new ToolStripMenuItem("(no guests yet)") { Tag = "player", Enabled = false };
            menu.Items.Insert(i, none);
        }
    }

    public void ShowToast(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}

