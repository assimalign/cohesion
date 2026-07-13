using System;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// A SQL command: statement text plus its bound parameters, executed against an
/// <see cref="ISqlConnection"/>.
/// </summary>
/// <remarks>
/// A command is a mutable, reusable request object separate from any internal plan
/// structure — the client sends the text and parameters over the wire, and the
/// server's SQL session parses and plans them. Reuse a command across executions by
/// rebinding its <see cref="Parameters"/>.
/// </remarks>
public sealed class SqlCommand
{
    /// <summary>
    /// Initializes a new <see cref="SqlCommand"/>.
    /// </summary>
    /// <param name="commandText">The SQL statement text.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="commandText"/> is null or whitespace.</exception>
    public SqlCommand(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        CommandText = commandText;
    }

    /// <summary>
    /// Gets or sets the SQL statement text.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when set to null or whitespace.</exception>
    public string CommandText
    {
        get;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    }

    /// <summary>
    /// Gets the command's bound parameters.
    /// </summary>
    public SqlParameterCollection Parameters { get; } = new();

    /// <summary>
    /// Binds a parameter and returns this command, for fluent composition.
    /// </summary>
    /// <param name="name">The parameter name, with or without a leading sigil.</param>
    /// <param name="value">The value to bind; null binds a SQL null.</param>
    /// <returns>This command.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or whitespace.</exception>
    public SqlCommand WithParameter(string name, object? value)
    {
        Parameters.Add(name, value);
        return this;
    }
}
