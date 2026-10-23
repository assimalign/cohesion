namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Per-category logger created by <see cref="ConsoleLoggerProvider"/>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ConsoleLogger")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ConsoleLogger : Logger
{
    private readonly ConsoleLoggerProvider _provider;

    public ConsoleLogger(string category, ConsoleLoggerProvider provider)
        : base(category)
    {
        _provider = provider;
    }

    public override bool IsEnabled(LogLevel level) => base.IsEnabled(level) && !_provider.IsDisposed;

    protected override void WriteCore(ILoggerEntry entry) => _provider.Write(entry);

    protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
    {
        // Emit the seed so the developer sees scope-open events in the console stream.
        if (IsEnabled(entry.Level))
        {
            _provider.Write(entry);
        }

        return new ConsoleScopedLogger(Category, _provider, entry.Id);
    }

    [global::System.Diagnostics.DebuggerDisplay("ConsoleScopedLogger")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class ConsoleScopedLogger : ScopedLogger
    {
        private readonly ConsoleLoggerProvider _provider;

        public ConsoleScopedLogger(string category, ConsoleLoggerProvider provider, LogId parentId)
            : base(category, parentId)
        {
            _provider = provider;
        }

        public override bool IsEnabled(LogLevel level) => base.IsEnabled(level) && !_provider.IsDisposed;

        protected override void WriteCore(ILoggerEntry entry) => _provider.Write(entry);

        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            if (IsEnabled(entry.Level))
            {
                _provider.Write(entry);
            }

            return new ConsoleScopedLogger(Category, _provider, entry.Id);
        }

        private sealed class DebugView
        {
            private readonly ConsoleScopedLogger _target;

            public DebugView(ConsoleScopedLogger value)
            {
                _target = value;
            }

            public LogId ParentId => _target.ParentId;
            public string Category => _target.Category;
        }
    }

    private sealed class DebugView
    {
        private readonly ConsoleLogger _target;

        public DebugView(ConsoleLogger value)
        {
            _target = value;
        }

        public string Category => _target.Category;
    }
}
