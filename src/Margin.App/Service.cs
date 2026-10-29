using System.Diagnostics;
using Margin.Core;

namespace Margin.App;

/// <summary>
/// Starts the companion service (service/, Python) when it is not already running: the checkout's
/// virtual environment, `python -m margin_service.server`. It is stopped with the app.
/// </summary>
static class Service
{
    static Process? process;

    public static async Task<bool> Ensure(Companion c)
    {
        if (await c.Alive()) return true;
        var root = FindRoot();
        if (root is null) return false;
        string py = Path.Combine(root, "service", ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(py)) return false;
        lock (typeof(Service))
        {
            process ??= Process.Start(new ProcessStartInfo(py, "-m margin_service.server")
            {
                WorkingDirectory = Path.Combine(root, "service"),
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        // Loading the language model takes a while.
        for (int i = 0; i < 180; i++)
        {
            if (await c.Alive()) return true;
            if (process is { HasExited: true }) return false;
            await Task.Delay(1000);
        }
        return false;
    }

    public static void Stop()
    {
        try { process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    static string? FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "service", "margin_service"))) d = d.Parent;
        return d?.FullName;
    }
}
