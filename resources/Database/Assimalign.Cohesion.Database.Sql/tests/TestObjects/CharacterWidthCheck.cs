namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>Observes whether a domain check receives its declared CHAR width.</summary>
internal sealed class CharacterWidthCheck : SqlScalarFunction
{
    internal CharacterWidthCheck()
        : base("character_width_check", [SqlType.Char(1)], SqlType.Boolean, SqlFunctionVolatility.Immutable)
    {
    }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        => SqlValue.FromBoolean(arguments.GetString(0).Length == 1);
}
