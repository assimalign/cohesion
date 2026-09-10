using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

internal static class InProcessConsoleRouter
{
    private static readonly Lock Sync = new();
    private static TextWriter? _originalOutput;
    private static TextWriter? _originalError;
    private static AmbientPrefixTextWriter? _routedOutput;
    private static AmbientPrefixTextWriter? _routedError;
    private static TextWriter? _installedOutput;
    private static TextWriter? _installedError;
    private static int _leaseCount;

    internal static IDisposable Acquire()
    {
        lock (Sync)
        {
            if (_leaseCount++ == 0)
            {
                _originalOutput = Console.Out;
                _originalError = Console.Error;
                _routedOutput = new AmbientPrefixTextWriter(_originalOutput);
                _routedError = new AmbientPrefixTextWriter(_originalError);
                Console.SetOut(_routedOutput);
                Console.SetError(_routedError);
                _installedOutput = Console.Out;
                _installedError = Console.Error;
            }
        }

        return new ConsoleRouterLease();
    }

    private static void Release()
    {
        lock (Sync)
        {
            if (_leaseCount == 0 || --_leaseCount != 0)
            {
                return;
            }

            _routedOutput!.FlushPending();
            _routedError!.FlushPending();
            if (ReferenceEquals(Console.Out, _installedOutput))
            {
                Console.SetOut(_originalOutput!);
            }
            if (ReferenceEquals(Console.Error, _installedError))
            {
                Console.SetError(_originalError!);
            }
            _originalOutput = null;
            _originalError = null;
            _routedOutput = null;
            _routedError = null;
            _installedOutput = null;
            _installedError = null;
        }
    }

    private sealed class ConsoleRouterLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release();
            }
        }
    }
}

internal sealed class AmbientPrefixTextWriter : TextWriter
{
    private const int MaximumPendingLineLength = 16 * 1024;

    private readonly Dictionary<ResourceContext, PendingLine> _pending =
        new(ReferenceEqualityComparer.Instance);
    private readonly TextWriter _inner;
    private readonly Lock _writeLock = new();

    internal AmbientPrefixTextWriter(TextWriter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override Encoding Encoding => _inner.Encoding;

    public override IFormatProvider FormatProvider => _inner.FormatProvider;

    public override void Flush()
    {
        ResourceContext context = ResourceRuntime.Current;
        lock (_writeLock)
        {
            if (_pending.TryGetValue(context, out PendingLine? line))
            {
                FlushLine(context.ResourceName!, line);
                _pending.Remove(context);
            }
            _inner.Flush();
        }
    }

    public override Task FlushAsync()
    {
        Flush();
        return Task.CompletedTask;
    }

    public override void Write(char value)
    {
        Span<char> character = stackalloc char[1];
        character[0] = value;
        WriteCore(character);
    }

    public override void Write(char[]? buffer, int index, int count)
    {
        if (buffer is null)
        {
            return;
        }

        WriteCore(buffer.AsSpan(index, count));
    }

    public override void Write(string? value)
    {
        if (value is not null)
        {
            WriteCore(value.AsSpan());
        }
    }

    public override Task WriteAsync(char value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    private void WriteCore(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        ResourceContext context = ResourceRuntime.Current;
        string? resource = context.ResourceName;
        lock (_writeLock)
        {
            if (string.IsNullOrWhiteSpace(resource))
            {
                _inner.Write(value);
                return;
            }

            if (!_pending.TryGetValue(context, out PendingLine? line))
            {
                line = new PendingLine();
                _pending.Add(context, line);
            }

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                line.Value.Append(character);
                if (character == '\n' || line.Value.Length >= MaximumPendingLineLength)
                {
                    FlushLine(resource, line);
                }
            }
            if (line.Value.Length == 0)
            {
                _pending.Remove(context);
            }
        }
    }

    internal void FlushPending()
    {
        lock (_writeLock)
        {
            foreach ((ResourceContext context, PendingLine line) in _pending)
            {
                FlushLine(context.ResourceName!, line);
            }
            _pending.Clear();
            _inner.Flush();
        }
    }

    private void FlushLine(string resource, PendingLine line)
    {
        if (line.Value.Length == 0)
        {
            return;
        }

        _inner.Write('[');
        _inner.Write(resource);
        _inner.Write("] ");
        _inner.Write(line.Value.ToString());
        line.Value.Clear();
    }

    private sealed class PendingLine
    {
        internal StringBuilder Value { get; } = new();
    }
}
