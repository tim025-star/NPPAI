using System.Diagnostics;
using NPPAI.Engine;

internal static class RuntimeLifecycleTests
{
    internal static async Task Run(Action<bool, string> assert, string packagePath = "artifacts/NPPAI")
    {
        string package = Path.GetFullPath(packagePath);
        var before = Process.GetProcessesByName("llama-server").Select(p => p.Id).ToHashSet();
        using var runtime = new LlamaRuntime(package);
        using var first = runtime.CreateClient("qwen2.5:3b", 1000);
        using var second = runtime.CreateClient("qwen2.5:3b", 1000);
        using var cancellation = new CancellationTokenSource(10);
        try { await first.PingAsync(cancellation.Token); throw new Exception("Expected cancelled model caller"); }
        catch (OperationCanceledException) { assert(true, "caller can cancel while the bundled model is loading"); }
        await Task.WhenAll(first.PingAsync(), second.PingAsync());
        var owned = Process.GetProcessesByName("llama-server").Where(p => !before.Contains(p.Id) &&
            p.MainModule!.FileName!.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
        assert(owned.Length == 1, "concurrent requests share one owned server after caller cancellation");
        var crt = owned[0].Modules.Cast<ProcessModule>().Where(m => m.ModuleName.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase) ||
            (m.ModuleName.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals("libc++.dll", StringComparison.OrdinalIgnoreCase))).ToArray();
        assert(crt.Length > 0 && crt.All(m => m.FileName.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
            "llama.cpp uses the packaged C++ runtime");
        assert(runtime.Status.Contains("Ready"), "bundled model reports readiness");
        Console.WriteLine("Runtime: " + runtime.Status);
        runtime.Dispose();
        foreach (var process in owned) {
            assert(process.WaitForExit(5000), "runtime disposal terminates its owned server"); process.Dispose();
        }
    }
}
