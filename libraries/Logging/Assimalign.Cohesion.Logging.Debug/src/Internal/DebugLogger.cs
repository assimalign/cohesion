namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Per-category logger created by <see cref="DebugLoggerProvider"/>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("DebugLogger")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class DebugLogger : Logger
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

    [global::System.Diagnostics.DebuggerDisplay("DebugScopedLogger")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class DebugScopedLogger : ScopedLogger
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

        private sealed class DebugView
        {
            private readonly DebugScopedLogger _target;

            public DebugView(DebugScopedLogger value)
            {
                _target = value;
            }

            public LogId ParentId => _target.ParentId;
            public string Category => _target.Category;
        }
    }

    private sealed class DebugView
    {
        private readonly DebugLogger _target;

        public DebugView(DebugLogger value)
        {
            _target = value;
        }

        public string Category => _target.Category;
    }
}
