using PassTheStick.Platform;

namespace PassTheStick.Core;

/// <summary>
/// One step of the input housekeeping loop. The caller runs <see cref="Step"/> every few milliseconds on a
/// dedicated thread: it pauses injection when the game is not in the foreground (releasing everything)
/// and lets the pipeline time out a holder who has gone quiet.
/// </summary>
public sealed class InputPump
{
    private readonly IWindowTracker _tracker;
    private readonly InputPipeline _pipeline;

    public InputPump(IWindowTracker tracker, InputPipeline pipeline)
    {
        _tracker = tracker;
        _pipeline = pipeline;
    }

    public void Step()
    {
        _pipeline.SetPaused(!(_tracker.IsPinned && _tracker.IsGameForeground));
        _pipeline.Tick();
    }
}
