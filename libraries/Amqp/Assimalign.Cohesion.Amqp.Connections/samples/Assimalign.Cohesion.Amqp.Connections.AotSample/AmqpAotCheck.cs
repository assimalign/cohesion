using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Connections.AotSample;

/// <summary>
/// One check of the guard: a name for the report and the work, which throws when the check fails.
/// </summary>
/// <param name="Name">The name the report prints.</param>
/// <param name="RunAsync">The check, run against the loopback connection.</param>
internal sealed record AmqpAotCheck(string Name, Func<AmqpAotLoopback, CancellationToken, Task> RunAsync);
