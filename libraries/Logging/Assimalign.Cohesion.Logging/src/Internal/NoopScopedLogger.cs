namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Substitute scoped logger used when an underlying provider throws while opening a scope. Keeps
/// the parent <see cref="CompositeLogger"/> stable in the face of misbehaving providers.
/// </summary>
internal sealed class NoopScopedLogger : ScopedLogger
{
    public static readonly NoopScopedLogger Instance = new();

    private NoopScopedLogger()
        : base(category: "noop", parentId: LogId.Empty)
    {
    }

    public override bool IsEnabled(LogLevel level) => false;

    protected override void WriteCore(ILoggerEntry entry)
    {
    }

    protected override IScopedLogger BeginScopeCore(ILoggerEntry entry) => this;
}
