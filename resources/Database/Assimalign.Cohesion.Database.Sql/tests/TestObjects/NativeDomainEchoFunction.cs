using System;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class NativeDomainEchoFunction : SqlScalarFunction
{
    private readonly Action _onInvoke;

    internal NativeDomainEchoFunction(SqlType domain, Action onInvoke)
        : base("native_domain_echo", [domain, SqlType.Integer], SqlType.Integer)
    {
        _onInvoke = onInvoke;
    }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        _onInvoke();
        return arguments[0];
    }
}
