using Avalonia;

namespace Margin.App;

static class Program
{
    /// <summary>
    /// Optional command line, mainly for the README's screenshots:
    ///   --book file.epub        open this book
    ///   --at 0.3                go to this fraction of the book (and count it as read)
    ///   --select "text"         select the first occurrence of this text on the page
    ///   --ask "question"        ask the companion about it
    ///   --tab characters        show this tab of the companion panel
    ///   --screenshot file.png   render the window to a PNG and exit
    ///   --wait 1.5              seconds to wait before the screenshot (summaries take a while)
    /// </summary>
    public static Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    [STAThread]
    public static void Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--"))
                Options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
