using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PassTheStick.App.Services;
using PassTheStick.App.ViewModels;
using PassTheStick.Windows;

namespace PassTheStick.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The mutex lives for the whole process and is disposed in OnExit.")]
public partial class App : Application
{
    private const string SingleInstanceName = "Local\\PassTheStick.SingleInstance";
    private Mutex? _singleInstance;
    private IHost? _host;
    private DispatcherTimer? _gameTimer;
    private TrayService? _tray;
    private OverlayController? _overlay;
    private HotkeyHost? _hotkeys;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, SingleInstanceName, out var first);
        if (!first)
        {
            MessageBox.Show("PassTheStick is already running. Look for its window or its icon in the taskbar.", "PassTheStick", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var store = new SettingsStore(SettingsStore.DefaultPath);
        var loaded = store.Load();

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders().AddDebug())
            .ConfigureServices(s =>
            {
                s.AddSingleton(store);
                s.AddSingleton(loaded.Settings);
                s.AddSingleton<IUiThread>(new DispatcherUiThread(Dispatcher));
                s.AddSingleton<IClipboardService, WpfClipboard>();
                s.AddSingleton<DeferredNotifier>();
                s.AddSingleton<INotifier>(sp => sp.GetRequiredService<DeferredNotifier>());
                s.AddSingleton<IHostSession>(sp => new HostSession(store, sp.GetRequiredService<ILoggerFactory>()));
                s.AddSingleton(sp => new MainViewModel(
                    sp.GetRequiredService<IHostSession>(),
                    sp.GetRequiredService<IUiThread>(),
                    sp.GetRequiredService<IClipboardService>(),
                    sp.GetRequiredService<INotifier>(),
                    store,
                    loaded.Settings,
                    loaded.Notice));
            })
            .Build();

        RecoverHidHide();

        ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.Invoke(ApplySystemTheme);

        var vm = _host.Services.GetRequiredService<MainViewModel>();
        var session = _host.Services.GetRequiredService<IHostSession>();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Closing += (_, args) =>
        {
            if (!_exiting && vm.ShouldHideOnClose)
            {
                args.Cancel = true;
                window.Hide();
                _tray?.Show("PassTheStick is still hosting", "It stays in the notification area. Use its icon to open it again or end the session.");
            }
        };

        _tray = new TrayService(vm, ShowMainWindow, ExitApp);
        _host.Services.GetRequiredService<DeferredNotifier>().Attach(_tray);
        _overlay = new OverlayController(session, vm);

        var settings = _host.Services.GetRequiredService<AppSettings>();
        _hotkeys = new HotkeyHost();
        _hotkeys.PassPressed += () => Dispatcher.BeginInvoke(() => vm.PassNextCommand.Execute(null));
        _hotkeys.TakeBackPressed += () => Dispatcher.BeginInvoke(() => vm.TakeBackCommand.Execute(null));
        var pass = HotkeyBinding.TryParse(settings.PassHotkey, out var passBinding) ? passBinding : HotkeyBinding.DefaultPass;
        var back = HotkeyBinding.TryParse(settings.TakeBackHotkey, out var backBinding) ? backBinding : HotkeyBinding.DefaultTakeBack;
        vm.ReportHotkeyProblems(_hotkeys.Register(pass, back));

        window.Show();

        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _gameTimer.Tick += (_, _) => vm.RefreshGame();
        _gameTimer.Start();
    }

    private void ShowMainWindow()
    {
        MainWindow?.Show();
        MainWindow!.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _gameTimer?.Stop();
        _hotkeys?.Dispose();
        _overlay?.Dispose();
        _tray?.Dispose();
        if (_host is not null)
        {
            // Ending the session releases every held key and button before the process goes away.
            _host.Services.GetRequiredService<IHostSession>().StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }

        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A crash while a pad was hidden would leave it hidden. Undo anything the last run left behind.</summary>
    private void RecoverHidHide()
    {
        var log = _host!.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HidHide");
        try
        {
            var journal = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "hidhide.json");
            if (File.Exists(journal))
            {
                using var guard = new HidHideGuard(new NefariusHidHideService(), journal, log);
                guard.RecoverLeftovers();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogHidHideCheckFailed(log, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check HidHide for leftovers")]
    private static partial void LogHidHideCheckFailed(ILogger logger, Exception ex);

    private void ApplySystemTheme()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v != 0;
        var merged = Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Tokens.Light", StringComparison.Ordinal) == true
            || d.Source?.OriginalString.Contains("Tokens.Dark", StringComparison.Ordinal) == true);
        var next = new ResourceDictionary { Source = new Uri(light ? "Themes/Tokens.Light.xaml" : "Themes/Tokens.Dark.xaml", UriKind.Relative) };
        if (existing is null)
        {
            merged.Insert(1, next);
        }
        else
        {
            merged[merged.IndexOf(existing)] = next;
        }
    }
}

internal sealed class DispatcherUiThread(Dispatcher dispatcher) : IUiThread
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

internal sealed class WpfClipboard : IClipboardService
{
    public bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false; // another app holds the clipboard
        }
    }
}

/// <summary>The view model is built before the tray icon exists, so it talks to this and the tray is attached afterwards.</summary>
internal sealed class DeferredNotifier : INotifier
{
    private INotifier? _target;

    public void Attach(INotifier target) => _target = target;

    public void Show(string title, string text) => _target?.Show(title, text);
}
