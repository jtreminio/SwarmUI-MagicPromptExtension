using System.Diagnostics;
using System.IO;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

/// <summary>Starts an installed Linux decision runtime as a child of SwarmUI.</summary>
internal static class DecisionRuntimeHost
{
    internal static Process Start(string extensionPath, string modelRoots)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var runtime = (modelRoots ?? "Models").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(root => Path.GetFullPath(Path.Combine(root, "decision-runtime")))
                .FirstOrDefault(path => File.Exists(Path.Combine(path, "venv", "bin", "startlux-decide")));
            if (runtime == null) return null;

            ProcessStartInfo start = new("/bin/bash") { UseShellExecute = false, WorkingDirectory = runtime };
            start.ArgumentList.Add(Path.GetFullPath(Path.Combine(extensionPath, "DecisionRuntime", "run.sh")));
            start.ArgumentList.Add(runtime);
            Logs.Info($"MagicPrompt: starting local decision runtime from {runtime}.");
            return Process.Start(start);
        }
        catch (Exception ex)
        {
            Logs.Warning($"MagicPrompt: could not launch the decision runtime: {ex.Message}");
            return null;
        }
    }

    internal static void Stop(Process process)
    {
        if (process == null) return;
        try
        {
            if (process.HasExited) return;
            // Let run.sh stop and reap its children before falling back to killing the tree.
            ProcessStartInfo terminate = new("/usr/bin/kill") { UseShellExecute = false };
            terminate.ArgumentList.Add("-TERM");
            terminate.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using Process signal = Process.Start(terminate);
            if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Logs.Warning($"MagicPrompt: could not stop the decision runtime: {ex.Message}");
        }
    }
}
