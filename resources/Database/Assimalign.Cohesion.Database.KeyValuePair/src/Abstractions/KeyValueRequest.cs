using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// The base of the key-value model's typed requests — the model's members of the
/// shared <see cref="QueryRequest"/> family. A key-value session executes these
/// through the root's typed seam (<c>IDatabaseSession.ExecuteAsync(QueryRequest, …)</c>);
/// the text seam parses the command grammar (<c>docs/COMMANDS.md</c>) into the
/// same request types, so both paths execute identically.
/// </summary>
public abstract class KeyValueRequest : QueryRequest<KeyValueStatement>
{
    private protected KeyValueRequest(KeyValueStatement statement)
        : base(statement)
    {
    }
}
