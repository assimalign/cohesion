using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// A logger factory that fails on purpose: either <see cref="Create"/> throws, or it hands out loggers
/// whose <see cref="ILogger.Log"/> throws.
/// </summary>
internal sealed class FaultingLoggerFactory : ILoggerFactory
{
    private readonly bool _throwOnCreate;

    public FaultingLoggerFactory(bool throwOnCreate)
    {
        _throwOnCreate = throwOnCreate;
    }

    /// <summary>The number of entries the handed-out loggers were asked to write.</summary>
    public int LogAttempts { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<ILoggerProvider> Providers { get; } = [];

    /// <inheritdoc />
    public ILogger Create(string category)
        => _throwOnCreate
            ? throw new InvalidOperationException("The factory refused to create a logger.")
            : new FaultingLogger(this);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class FaultingLogger : ILogger
    {
        private readonly FaultingLoggerFactory _factory;

        public FaultingLogger(FaultingLoggerFactory factory)
        {
            _factory = factory;
        }

        public bool IsEnabled(LogLevel level) => level != LogLevel.None;

        public void Log(ILoggerEntry entry)
        {
            _factory.LogAttempts++;
            throw new InvalidOperationException("The sink failed.");
        }

        public IScopedLogger BeginScope(ILoggerEntry entry) => throw new NotSupportedException();
    }
}
