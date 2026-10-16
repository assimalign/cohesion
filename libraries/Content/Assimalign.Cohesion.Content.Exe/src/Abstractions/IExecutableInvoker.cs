using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>Invokes executable paths without parsing their formats or using a shell.</summary>
public interface IExecutableInvoker
{
    /// <summary>Runs a process and returns after termination and owned-resource cleanup.</summary>
    /// <param name="startInfo">Path, argument list and lifecycle/capture options.</param>
    /// <param name="cancellationToken">Cancellation returns a canceled result after process-tree cleanup.</param>
    /// <returns>Exit, timeout, cancellation or failed-start result with bounded output.</returns>
    /// <exception cref="ArgumentNullException">Start information is null.</exception>
    /// <remarks>Unexpected I/O or cleanup errors propagate after cleanup. Failed OS launches return FailedToStart.</remarks>
    /// <exception cref="System.IO.IOException">Unexpected redirected-pipe I/O fails after cleanup is attempted.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The operating system refuses process-tree cleanup.</exception>
    /// <exception cref="System.InvalidOperationException">Process lifecycle state prevents cleanup from completing.</exception>
    ValueTask<ExecutableResult> InvokeAsync(ExecutableStartInfo startInfo, CancellationToken cancellationToken = default);
}
