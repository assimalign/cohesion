using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

/// <summary>
/// One run of a resource's program: its entry point on a background thread of its own, in an
/// environment of its own, until its scope's shutdown stops it.
/// </summary>
internal sealed class InProcessWorkload
{
    private readonly InProcessProgram _program;
    private readonly AppEnvironmentScope _scope;
    private readonly CancellationTokenSource _shutdown;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private InProcessWorkload(
        InProcessProgram program,
        IReadOnlyDictionary<SettingName, string> settings,
        int endpointPort,
        AppEnvironmentScope scope,
        CancellationTokenSource shutdown)
    {
        _program = program;
        _scope = scope;
        _shutdown = shutdown;
        Settings = settings;
        EndpointPort = endpointPort;
    }

    /// <summary>Gets the settings the program runs with.</summary>
    public IReadOnlyDictionary<SettingName, string> Settings { get; }

    /// <summary>Gets the loopback port the program's endpoint listens on.</summary>
    public int EndpointPort { get; }

    /// <summary>Gets whether the program's entry point has returned or thrown.</summary>
    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>
    /// Starts the program's entry point on a new background thread that enters the program's
    /// environment first.
    /// </summary>
    /// <remarks>
    /// The thread starts without the caller's execution context, so the program inherits nothing
    /// ambient from the gateway; it runs in its scope alone. A background thread does not keep the
    /// process alive, as a workload does not keep its platform's gateway alive.
    /// </remarks>
    public static InProcessWorkload Start(
        InProcessProgram program,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<SettingName, string> settings,
        int endpointPort)
    {
        var shutdown = new CancellationTokenSource();
        var workload = new InProcessWorkload(program, settings, endpointPort, AppEnvironmentScope.Create(variables, shutdown.Token), shutdown);
        var thread = new Thread(workload.Run)
        {
            IsBackground = true,
            Name = string.Concat(program.Provider.Value, "/", program.Name.Value),
        };

        thread.UnsafeStart();
        return workload;
    }

    /// <summary>
    /// Whether the program runs with exactly these settings.
    /// </summary>
    public bool Runs(IReadOnlyDictionary<SettingName, string> settings)
    {
        if (Settings.Count != settings.Count)
        {
            return false;
        }

        foreach (KeyValuePair<SettingName, string> setting in settings)
        {
            if (!Settings.TryGetValue(setting.Key, out string? value) || !string.Equals(value, setting.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Signals the program's shutdown and waits for its entry point to return.
    /// </summary>
    /// <exception cref="InvalidOperationException">The program did not stop within <paramref name="timeout"/>.</exception>
    public async Task StopAsync(TimeSpan timeout)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _exited.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception) when (_exited.Task.IsFaulted)
        {
            // The program failed, while stopping or before: either way it has stopped. Checked first,
            // so a program that threw a TimeoutException of its own is not taken for one that hung.
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ProgramStopTimedOut, _program.Provider, _program.Name, timeout));
        }
    }

    private void Run()
    {
        using (AppEnvironment.Enter(_scope))
        {
            try
            {
                _program.Main.Invoke([]).GetAwaiter().GetResult();
                _exited.TrySetResult();
            }
            catch (Exception exception)
            {
                // Deliberately broad: whatever the program threw ends its run, as a crash ends a
                // process; nothing escapes its thread.
                _exited.TrySetException(exception);
            }
        }
    }
}
