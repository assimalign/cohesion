# `Assimalign.Cohesion.Logging.LoggerBase`

Reusable abstract base class for `ILogger` implementations. Folds the per-call boilerplate
(null guards, level short-circuit) into non-virtual methods so derived classes only implement
the actual write and scope-creation work.

## Constructor

```csharp
protected LoggerBase(string category);
```

Throws `ArgumentException` when `category` is null or empty.

## Members

| Member | Description |
| --- | --- |
| `string Category { get; }` | The category the logger was created for. |
| `virtual bool IsEnabled(LogLevel level)` | Defaults to "any level except `None`". Override to factor in provider disposal or external state. |
| `void Log(ILoggerEntry entry)` | Non-virtual. Null-checks, calls `IsEnabled`, and dispatches to `WriteCore`. |
| `IScopedLogger BeginScope(ILoggerEntry entry)` | Non-virtual. Null-checks and dispatches to `BeginScopeCore`. |
| `protected abstract void WriteCore(ILoggerEntry entry)` | Derived classes implement the actual write. |
| `protected abstract IScopedLogger BeginScopeCore(ILoggerEntry entry)` | Derived classes implement scope creation. Derived classes that want the seed entry to appear in their sink output MUST call `WriteCore` explicitly before constructing the scope. |

## Devirtualization

`Log` and `BeginScope` are non-virtual; callers holding a strongly typed `LoggerBase` (or a
sealed derived) reference pay a single virtual dispatch to `WriteCore` / `BeginScopeCore`
instead of two through the interface. Concrete implementations should be `sealed`.

## Example

```csharp
internal sealed class MyLogger : LoggerBase
{
    private readonly MyProvider _provider;

    public MyLogger(string category, MyProvider provider) : base(category)
    {
        _provider = provider;
    }

    public override bool IsEnabled(LogLevel level)
        => base.IsEnabled(level) && !_provider.IsDisposed;

    protected override void WriteCore(ILoggerEntry entry) => _provider.Emit(entry);

    protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
    {
        if (IsEnabled(entry.Level))
        {
            _provider.Emit(entry); // emit seed
        }
        return new MyScopedLogger(Category, entry.Id, _provider);
    }
}
```
