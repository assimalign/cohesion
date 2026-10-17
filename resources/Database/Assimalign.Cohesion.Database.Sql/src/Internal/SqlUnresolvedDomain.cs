namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A stored column whose domain the engine that opened the database does not resolve (phase E3):
/// the catalog keeps the domain's name, and the engine registers no domain of the name, or one over
/// another storage type. As owner decision 65 has it for a function a CHECK calls, the database
/// opens, its reads proceed and each write to the column fails with <c>COHSQLE010</c>.
/// </summary>
/// <param name="Table">The schema-qualified table, for example <c>dbo.contacts</c>.</param>
/// <param name="Column">The column.</param>
/// <param name="Domain">The domain's name as the catalog keeps it.</param>
/// <param name="Reason">Why it does not resolve, completing the sentence <see cref="Describe"/> builds.</param>
internal sealed record SqlUnresolvedDomain(string Table, string Column, string Domain, string Reason)
{
    /// <summary>Describes the column and its domain for a message: <c>Column 'email' of table 'dbo.contacts' is of domain 'email', which this engine does not register</c>.</summary>
    /// <returns>The description, without a final period.</returns>
    internal string Describe() => $"Column '{Column}' of table '{Table}' is of domain '{Domain}', {Reason}";
}
