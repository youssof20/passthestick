namespace PassTheStick.App.Services;

public static class OverlayPlacement
{
    /// <summary>Top-left corner for an overlay of the given size: inside the game window's top-left, inset by the margin, and never outside the window.</summary>
    public static (double X, double Y) Compute((int Left, int Top, int Width, int Height) game, double width, double height, double margin)
    {
        var x = game.Left + margin;
        var y = game.Top + margin;
        var maxX = game.Left + game.Width - width;
        var maxY = game.Top + game.Height - height;
        return (Math.Max(game.Left, Math.Min(x, maxX)), Math.Max(game.Top, Math.Min(y, maxY)));
    }
}
