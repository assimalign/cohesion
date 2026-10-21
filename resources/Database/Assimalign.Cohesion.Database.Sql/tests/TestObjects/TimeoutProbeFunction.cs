using System;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class TimeoutProbeFunction : SqlScalarFunction
{
    internal TimeoutProbeFunction() : base("timeout_probe", [SqlType.Integer], SqlType.Integer)
    {
    }

    internal Action<CancellationToken>? Callback { get; set; }

    internal int Calls { get; private set; }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        Calls++;
        Callback?.Invoke(arguments.Context.CancellationToken);
        return arguments[0];
    }
}
