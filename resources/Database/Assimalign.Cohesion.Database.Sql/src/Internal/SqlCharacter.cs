using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Keeps CHAR identity inside evaluated rows. Public results and tuple payloads remain strings;
/// erasing this identity before comparison would make CHAR indistinguishable from VARCHAR.
/// </summary>
internal sealed class SqlCharacter
{
    internal SqlCharacter(string value)
    {
        Value = value;
        Text = value.TrimEnd(' ');
    }

    internal string Value { get; }
    internal string Text { get; }

    // Text conversion, including concatenation, strips padding. LIKE and result output use Value.
    public override string ToString() => Text;

    internal static object? Output(object? value) => value is SqlCharacter character ? character.Value : value;
    internal static object? Input(object? value, Types.DatabaseType type)
        => value is string text && type == Types.DatabaseType.Character ? new SqlCharacter(text) : value;
}
