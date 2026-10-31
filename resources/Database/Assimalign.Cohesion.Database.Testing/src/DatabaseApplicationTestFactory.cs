using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Testing;

/// <summary>
/// Invokes a Cohesion database program's real <c>Program.Main</c> inside an
/// <see cref="AppEnvironmentScope"/> of its own, and stops it through that scope's shutdown signal.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FromProgram{TProgram}()"/> uses <c>TProgram</c> only as the statically rooted
/// entry-assembly marker. It invokes <see cref="Assembly.EntryPoint"/>, the one reflection
/// operation sanctioned for compiler-synthesized top-level entry points. The generic
/// preservation annotation retains public and compiler-generated non-public methods for
/// trimming and NativeAOT; the factory performs no assembly scanning, dynamic loading, or
/// runtime code generation.
/// </para>
/// <para>
/// A top-level program must expose its compiler-generated marker to its test project
/// with <c>public partial class Program { }</c>. Its normal top-level statements remain the
/// only composition path; the factory does not require a test-only bootstrap or interface.
/// </para>
/// <para>
/// The program runs on a background thread of its own, which starts without the caller's
/// execution context and enters a fresh scope first, so the program inherits nothing ambient from
/// the test. A host the program runs inside that scope stops when <see cref="StopAsync"/> signals
/// the scope's shutdown, not on the process's shutdown signals.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("DatabaseApplicationTestFactory")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Testing.DatabaseApplicationTestFactory.DebugView))]
public sealed class DatabaseApplicationTestFactory : IDatabaseApplicationTestFactory
{
    private readonly MethodInfo _entryPoint;
    private readonly string[] _arguments;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _program;
    private bool _isStarted;
    private bool _isDisposed;

    private DatabaseApplicationTestFactory(
        MethodInfo entryPoint,
        DatabaseApplicationTestFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Arguments);

        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ShutdownTimeout,
                ErrorMessages.DatabaseApplicationTestFactoryFailure2);
        }

        _entryPoint = entryPoint;
        _arguments = (string[])options.Arguments.Clone();
        _shutdownTimeout = options.ShutdownTimeout;
    }

    /// <inheritdoc />
    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _isStarted;
            }
        }
    }

    /// <summary>
    /// Creates a factory for the program declared by <typeparamref name="TProgram"/>, with default
    /// options.
    /// </summary>
    /// <typeparam name="TProgram">
    /// The executable's top-level <c>Program</c> marker. Declare it public and partial in the
    /// executable project so the test project can name it.
    /// </typeparam>
    /// <returns>An unstarted factory for the program.</returns>
    /// <exception cref="InvalidOperationException">
    /// The marker's assembly has no supported static entry point, or its entry point is not
    /// declared by <typeparamref name="TProgram"/>.
    /// </exception>
    public static DatabaseApplicationTestFactory FromProgram<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)] TProgram>()
    {
        return FromProgram<TProgram>(new DatabaseApplicationTestFactoryOptions());
    }

    /// <summary>
    /// Creates a factory for the program declared by <typeparamref name="TProgram"/> with explicit
    /// invocation options.
    /// </summary>
    /// <typeparam name="TProgram">
    /// The executable's top-level <c>Program</c> marker. Declare it public and partial in the
    /// executable project so the test project can name it.
    /// </typeparam>
    /// <param name="options">The arguments and shutdown budget for the invocation.</param>
    /// <returns>An unstarted factory for the program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The shutdown timeout is not positive.</exception>
    /// <exception cref="InvalidOperationException">
    /// The marker's assembly has no supported static entry point, or its entry point is not
    /// declared by <typeparamref name="TProgram"/>.
    /// </exception>
    public static DatabaseApplicationTestFactory FromProgram<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)] TProgram>(
        DatabaseApplicationTestFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        MethodInfo entryPoint = ResolveEntryPoint<TProgram>();
        return new DatabaseApplicationTestFactory(entryPoint, options);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_program is null)
            {
                _program = Launch();
                _isStarted = true;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? program;

        lock (_gate)
        {
            program = _program;
        }

        if (program is null)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await program.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _isStarted = false;
        }
    }

    /// <summary>
    /// Signals the program's shutdown when it was started and waits, up to the configured shutdown
    /// timeout, for its entry point to return. Disposal is idempotent.
    /// </summary>
    /// <returns>A task that completes when the factory has released the program.</returns>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        using var shutdown = new CancellationTokenSource(_shutdownTimeout);
        await StopAsync(shutdown.Token).ConfigureAwait(false);
    }

    // The entry point runs on a background thread of its own, as a process's main thread runs a
    // program: the thread enters the program's scope first, and a background thread does not keep
    // the test process alive.
    private Task Launch()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppEnvironmentScope scope = AppEnvironmentScope.Create(new Dictionary<string, string>(), _shutdown.Token);
        var thread = new Thread(() =>
        {
            using (AppEnvironment.Enter(scope))
            {
                try
                {
                    InvokeEntryPoint();
                    exited.TrySetResult();
                }
                catch (Exception exception)
                {
                    // Deliberately broad: whatever the program threw ends its run, as a crash ends
                    // a process. StopAsync surfaces it; nothing escapes the thread.
                    exited.TrySetException(exception);
                }
            }
        })
        {
            IsBackground = true,
            Name = _entryPoint.DeclaringType?.Assembly.GetName().Name,
        };

        thread.UnsafeStart();
        return exited.Task;
    }

    private void InvokeEntryPoint()
    {
        object?[]? arguments = _entryPoint.GetParameters().Length == 0
            ? null
            : new object?[] { (string[])_arguments.Clone() };

        object? result;
        try
        {
            result = _entryPoint.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }

        int exitCode = 0;
        switch (result)
        {
            case Task<int> exitTask:
                exitCode = exitTask.GetAwaiter().GetResult();
                break;
            case Task task:
                task.GetAwaiter().GetResult();
                break;
            case int code:
                exitCode = code;
                break;
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProgramExitCodeNonZero, exitCode));
        }
    }

    private static MethodInfo ResolveEntryPoint<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)] TProgram>()
    {
        Type programType = typeof(TProgram);
        MethodInfo entryPoint = programType.Assembly.EntryPoint
            ?? throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TestFactoryResolveEntryPointFailure, programType.Assembly.GetName().Name));

        if (!ReferenceEquals(entryPoint.DeclaringType, programType))
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TestFactoryResolveEntryPointFailure2, programType.FullName, programType.Assembly.GetName().Name));
        }
        if (!entryPoint.IsStatic)
        {
            throw new InvalidOperationException(ErrorMessages.TestFactoryResolveEntryPointFailure3);
        }

        ParameterInfo[] parameters = entryPoint.GetParameters();
        if (parameters.Length > 1 ||
            (parameters.Length == 1 && parameters[0].ParameterType != typeof(string[])))
        {
            throw new InvalidOperationException(
                ErrorMessages.TestFactoryResolveEntryPointFailure4);
        }

        Type returnType = entryPoint.ReturnType;
        if (returnType != typeof(void) &&
            returnType != typeof(int) &&
            returnType != typeof(Task) &&
            returnType != typeof(Task<int>))
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TestFactoryResolveEntryPointFailure5, returnType.FullName));
        }

        return entryPoint;
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Testing.DatabaseApplicationTestFactory value)
        {
            _ = value;
        }
    }
}
