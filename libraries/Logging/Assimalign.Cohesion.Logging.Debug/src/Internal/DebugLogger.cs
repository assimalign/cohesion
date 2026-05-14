namespace Assimalign.Cohesion.Logging.Debug.Internal;

/// <summary>
/// Per-category logger created by <see cref="DebugLoggerProvider"/>.
/// </summary>
internal sealed class DebugLogger : LoggerBase
{
    private readonly DebugLoggerProvider _provider;

    public DebugLogger(string category, DebugLoggerProvider provider)
        : base(category)
    {
        _provider = provider;
    }

    public override bool IsEnabled(LogLevel level) => _provider.IsEnabledFor(level);

    protected override void WriteCore(ILoggerEntry entry) => _provider.Write(entry);

    protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
    {
        if (IsEnabled(entry.Level))
        {
            _provider.Write(entry);
        }

        return new DebugScopedLogger(Category, _provider, entry.Id);
    }

    private sealed class DebugScopedLogger : ScopedLoggerBase
    {
        private readonly DebugLoggerProvider _provider;

        public DebugScopedLogger(string category, DebugLoggerProvider provider, LogId parentId)
            : base(category, parentId)
        {
            _provider = provider;
        }

        public override bool IsEnabled(LogLevel level) => !IsDisposed && _provider.IsEnabledFor(level);

        protected override void WriteCore(ILoggerEntry entry) => _provider.Write(entry);

        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            if (IsEnabled(entry.Level))
            {
                _provider.Write(entry);
            }

            return new DebugScopedLogger(Category, _provider, entry.Id);
        }
    }
}
