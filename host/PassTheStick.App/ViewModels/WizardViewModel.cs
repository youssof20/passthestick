using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassTheStick.App.Services;
using PassTheStick.Windows;

namespace PassTheStick.App.ViewModels;

public enum WizardStep
{
    Welcome,
    Game,
    Controls,
    Done,
}

/// <summary>
/// The short first-run flow: what this does, pick the game window, keyboard only or controllers too, done.
/// It never installs anything. If a controller driver is needed it says what to get and where.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    public const string DriverUrl = "https://github.com/nefarius/ViGEmBus/releases";

    private readonly IHostSession _session;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly Action<nint> _pin;

    public WizardViewModel(IHostSession session, SettingsStore store, AppSettings settings, Action<nint> pin)
    {
        _session = session;
        _store = store;
        _settings = settings;
        _pin = pin;
        RefreshWindows();
    }

    /// <summary>Raised when the wizard is finished or skipped; the argument is the chosen settings.</summary>
    public event Action<AppSettings>? Completed;

    public ObservableCollection<WindowInfo> Windows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsGame), nameof(IsControls), nameof(IsDone), nameof(CanGoBack), nameof(NextText))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private WizardStep _step = WizardStep.Welcome;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickCommand))]
    private WindowInfo? _selectedWindow;

    [ObservableProperty]
    private string? _pickedTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriverMessage), nameof(ShowDriverHelp))]
    private bool _wantsControllers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriverMessage), nameof(ShowDriverHelp))]
    private string? _driverProblem;

    [ObservableProperty]
    private string? _error;

    public bool IsWelcome => Step == WizardStep.Welcome;

    public bool IsGame => Step == WizardStep.Game;

    public bool IsControls => Step == WizardStep.Controls;

    public bool IsDone => Step == WizardStep.Done;

    public bool CanGoBack => Step != WizardStep.Welcome;

    public string NextText => Step == WizardStep.Done ? "Done" : "Next";

    public bool ShowDriverHelp => WantsControllers && DriverProblem is not null;

    public string DriverMessage => !WantsControllers
        ? "Keyboard only. Nothing to install."
        : DriverProblem is null
            ? "The controller driver is installed. Guests can have virtual controllers."
            : $"{DriverProblem} Get it from {DriverUrl} and restart your PC after installing. You can finish setup now and add it later.";

    [RelayCommand]
    private void Next()
    {
        Error = null;
        if (Step == WizardStep.Done)
        {
            Finish();
            return;
        }

        Step = Step + 1;
        if (Step == WizardStep.Controls)
        {
            CheckDriver();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step -= 1;

    [RelayCommand]
    private void Skip() => Finish();

    [RelayCommand]
    private void RefreshWindows()
    {
        var keep = SelectedWindow?.Handle;
        Windows.Clear();
        foreach (var w in _session.ListWindows())
        {
            Windows.Add(w);
        }

        SelectedWindow = Windows.FirstOrDefault(w => w.Handle == keep);
    }

    [RelayCommand(CanExecute = nameof(CanPick))]
    private void Pick()
    {
        try
        {
            _pin(SelectedWindow!.Handle);
            PickedTitle = SelectedWindow.Title;
            Error = null;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            RefreshWindows();
        }
    }

    [RelayCommand]
    private void CheckDriver() => DriverProblem = WantsControllers ? _session.CheckPadDriver() : null;

    partial void OnWantsControllersChanged(bool value) => CheckDriver();

    private bool CanPick() => SelectedWindow is not null;

    private void Finish()
    {
        var chosen = _settings with { OnboardingCompleted = true, UseVirtualPads = WantsControllers, LastGameProcess = SelectedWindow?.ProcessName ?? _settings.LastGameProcess };
        try
        {
            _store.Save(chosen);
        }
        catch (IOException)
        {
            // Settings could not be written. The choices still apply for this run.
        }

        Completed?.Invoke(chosen);
    }
}
