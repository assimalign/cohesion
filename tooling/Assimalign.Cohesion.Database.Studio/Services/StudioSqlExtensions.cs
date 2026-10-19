using System;
using System.Text;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// The Studio's own SQL extension, packaged as an application packages one: extension members on
/// the engine builder (engine extensibility design §3.8). The functions register through the same
/// <see cref="SqlDatabaseEngineBuilder.Functions"/> the engine's built-ins are registered in, so the
/// editor, the catalog explorer, the wire server and a declared CHECK all reach them as they reach
/// <c>UPPER</c>.
/// </summary>
internal static class StudioSqlExtensions
{
    /// <summary>The registered scalar: the upper-case first letter of each space-separated word.</summary>
    public const string InitialsFunction = "studio_initials";

    /// <summary>The registered aggregate: the checked product of a BIGINT column.</summary>
    public const string ProductFunction = "studio_product";

    /// <summary>The declared database's one table; its CHECK calls <see cref="InitialsFunction"/>.</summary>
    public const string NotesTable = "notes";

    /// <summary>The CHECK on <see cref="NotesTable"/>: a title must have at least one word.</summary>
    public const string NotesCheck = "ck_notes_initials";

    extension(SqlDatabaseEngineBuilder sql)
    {
        /// <summary>Registers <see cref="InitialsFunction"/> and <see cref="ProductFunction"/> on the engine.</summary>
        /// <returns>The engine builder, for chaining.</returns>
        public SqlDatabaseEngineBuilder AddStudioFunctions()
        {
            // Immutable, so a CHECK may call it and a constant call folds once at planning.
            sql.Functions.Add(SqlScalarFunction.Create<string, string>(InitialsFunction, Initials, SqlFunctionVolatility.Immutable));

            // A strict aggregate: NULL inputs are skipped, and a group with none returns NULL.
            sql.Functions.Add(SqlAggregateFunction.Create<long, long, long>(ProductFunction,
                seed: static () => 1L,
                step: static (state, value) => checked(state * value),
                finish: static state => state));
            return sql;
        }

        /// <summary>
        /// Declares <paramref name="name"/> with a typed schema whose CHECK calls a registered
        /// function, so the engine's build provisions it and binds that CHECK before it returns.
        /// </summary>
        /// <param name="name">The database name.</param>
        /// <returns>The engine builder, for chaining.</returns>
        public SqlDatabaseEngineBuilder AddStudioDatabase(string name)
            => sql.AddDatabase(name, database => database.Schema(schema =>
                schema.Table<StudioNote>(NotesTable, table =>
                {
                    table.Key(note => note.Id);
                    table.Column(note => note.Title);
                    table.Check(NotesCheck, $"{InitialsFunction}(Title) <> ''");
                })));
    }

    private static string Initials(string text)
    {
        var initials = new StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            initials.Append(char.ToUpperInvariant(word[0]));
        }

        return initials.ToString();
    }
}

/// <summary>A row of the declared database's <c>notes</c> table.</summary>
internal sealed record StudioNote(long Id, string Title);
