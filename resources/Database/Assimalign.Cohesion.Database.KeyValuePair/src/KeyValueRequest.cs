using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// The base of the key-value model's typed requests: the model's members of the shared
/// <see cref="DatabaseRequest"/> family, and the request root of
/// <see cref="KeyValueDatabaseSession"/> (<c>DatabaseExecutionSession&lt;KeyValueRequest&gt;</c>).
/// </summary>
/// <remarks>
/// The key-value model has no query language: a request is a typed operation with its operands,
/// and <see cref="Operation"/> names which one. A session executes these through its typed execute
/// member, the only execute member it has. The command grammar (<c>docs/COMMANDS.md</c>) is the
/// wire encoding of these requests: the model's wire server parses each command it receives into
/// the same request types, so both paths execute identically. All six leaves live in this
/// assembly, so the constructor is <c>private protected</c>.
/// </remarks>
public abstract class KeyValueRequest : DatabaseRequest
{
    private readonly KeyValueOperation _operation;

    /// <summary>
    /// Initializes a request for one of the model's operations.
    /// </summary>
    /// <param name="operation">The operation the request executes.</param>
    private protected KeyValueRequest(KeyValueOperation operation)
    {
        _operation = operation;
    }

    /// <summary>
    /// Gets the operation the request executes.
    /// </summary>
    public KeyValueOperation Operation => _operation;
}
