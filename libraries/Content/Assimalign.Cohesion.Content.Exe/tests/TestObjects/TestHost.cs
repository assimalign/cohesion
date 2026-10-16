using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

namespace Assimalign.Cohesion.Content.Exe.Tests;

internal static class TestHost
{
    internal static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "TestHost", "Assimalign.Cohesion.Content.Exe.TestHost.dll");

    internal static string DotnetPath
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (processPath is not null && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }
            var provided = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (provided is not null && File.Exists(provided))
            {
                return provided;
            }
            // VSTest can run in an apphost. Derive the matching dotnet installation from its loaded runtime.
            var candidate = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            File.Exists(candidate).ShouldBeTrue();
            return candidate;
        }
    }

    internal static string[] Arguments(params string[] arguments)
    {
        File.Exists(AssemblyPath).ShouldBeTrue();
        var all = new string[arguments.Length + 1];
        all[0] = AssemblyPath;
        arguments.CopyTo(all, 1);
        return all;
    }

    internal static string Decode(string text) => Encoding.UTF8.GetString(Convert.FromBase64String(text));

    internal static async Task<Process> WaitForProcessAsync(string path, Task invocation, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken);
                if (int.TryParse(text, out var pid))
                {
                    return Process.GetProcessById(pid);
                }
            }
            invocation.IsCompleted.ShouldBeFalse("the fixture must signal readiness before invocation terminates");
            await Task.Delay(20, cancellationToken);
        }
    }
}
