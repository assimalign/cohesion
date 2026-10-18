using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;

public sealed partial class SqlQueryParser
{
    /// <summary>
    /// Dispatches CREATE on its object word, including contextual SCHEMA, before the
    /// historical optional-TABLE fallback.
    /// </summary>
    private SqlQueryExpression ParseCreate(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume CREATE

        if (IsKeyword(ref lexer, "VIEW") || IsKeyword(ref lexer, "OR") || IsWord(ref lexer, "MATERIALIZED") ||
            IsWord(ref lexer, "RECURSIVE") || IsWord(ref lexer, "TEMP") || IsWord(ref lexer, "TEMPORARY"))
        {
            return ParseCreateView(ref lexer, pos);
        }
        if (IsWord(ref lexer, "SCHEMA"))
        {
            return ParseCreateSchema(ref lexer, pos);
        }

        if (!IsAtEnd(ref lexer) && (IsKeyword(ref lexer, "UNIQUE") || IsKeyword(ref lexer, "INDEX")))
        {
            return ParseCreateIndex(ref lexer, pos);
        }

        if (IsKeyword(ref lexer, "FUNCTION"))
        {
            return ParseCreateFunction(ref lexer, pos);
        }
        if (IsKeyword(ref lexer, "DOMAIN"))
        {
            return ParseCreateDomain(ref lexer, pos);
        }
        if (IsWord(ref lexer, "SEQUENCE"))
        {
            RejectIdentityConstruct(ref lexer, "CREATE SEQUENCE");
            return new SqlQueryExpression(SqlQueryCommandType.Create, null, Location.Create(1, 1, pos, _lastTokenEnd));
        }
        return ParseCreateTable(ref lexer, pos);
    }

    private SqlCreateIndexExpression ParseCreateIndex(ref TokenLexer lexer, int pos)
    {
        // UNIQUE
        bool isUnique = false;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "UNIQUE"))
        {
            isUnique = true;
            Advance(ref lexer);
        }

        // INDEX
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "INDEX"))
        {
            Advance(ref lexer);
        }

        bool ifNotExists = ParseExistenceGuard(ref lexer, notExists: true);

        // Index name. ON in its place means the name is missing: CREATE INDEX ON t (a)
        // used to create an index named ON.
        string indexName = "?";
        if (IsNameToken(ref lexer))
        {
            indexName = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
        }
        else
        {
            AddExpectedDiagnostic(ref lexer, "an index name");
        }

        // ON <table>
        ExpectKeyword(ref lexer, "ON", "ON and the indexed table");

        var table = ParseUnaliasedTableReference(ref lexer);

        // Key column list: ( col [, col]* ). Every element must name a column.
        var columns = new List<string>();
        if (Expect(ref lexer, TokenType.LeftParen, "'(' and the index columns"))
        {
            while (true)
            {
                if (!IsNameToken(ref lexer))
                {
                    AddExpectedDiagnostic(ref lexer, "an index column name");
                    break;
                }

                columns.Add(CurrentIdentifierText(ref lexer));
                Advance(ref lexer);
                if (lexer.Current.Type != TokenType.Comma)
                {
                    break;
                }
                Advance(ref lexer);
            }

            SkipToClosingParenthesis(ref lexer);
        }

        return new SqlCreateIndexExpression(indexName, table, columns, isUnique, ifNotExists, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    /// <summary>
    /// Parses <c>IF NOT EXISTS</c> (<paramref name="notExists"/>, the CREATE form) or
    /// <c>IF EXISTS</c> (the DROP form) when IF is current. A partial or mismatched
    /// sequence is an error: IF alone used to be skipped, so <c>DROP TABLE IF t</c>
    /// dropped t and <c>CREATE INDEX IF EXISTS</c> meant IF NOT EXISTS (#1068).
    /// </summary>
    private bool ParseExistenceGuard(ref TokenLexer lexer, bool notExists)
    {
        if (!IsKeyword(ref lexer, "IF"))
        {
            return false;
        }

        Advance(ref lexer);
        if (IsKeyword(ref lexer, "NOT") != notExists)
        {
            AddExpectedDiagnostic(ref lexer, notExists ? "NOT EXISTS after IF" : "EXISTS after IF");

            // Recover past the rest of the guard so the object name is still read as one.
            if (IsKeyword(ref lexer, "NOT"))
            {
                Advance(ref lexer);
            }
            if (IsKeyword(ref lexer, "EXISTS"))
            {
                Advance(ref lexer);
            }
            return notExists;
        }

        if (notExists)
        {
            Advance(ref lexer); // consume NOT
        }

        ExpectKeyword(ref lexer, "EXISTS", notExists ? "EXISTS after IF NOT" : "EXISTS after IF");
        return true;
    }

    private SqlCreateTableExpression ParseCreateTable(ref TokenLexer lexer, int pos)
    {
        // TABLE
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "TABLE"))
        {
            Advance(ref lexer);
        }

        bool ifNotExists = ParseExistenceGuard(ref lexer, notExists: true);

        // Table reference (no alias)
        var table = ParseUnaliasedTableReference(ref lexer);

        // Column definitions: ( col1 TYPE, col2 TYPE, ... )
        var columns = new List<SqlColumnDefinition>();
        var constraints = new List<SqlConstraintDefinition>();
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);

            while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.RightParen &&
                   lexer.Current.Type != TokenType.Semicolon)
            {
                if (IsConstraintStart(ref lexer))
                {
                    constraints.Add(ParseConstraint(ref lexer, null));
                }
                else
                {
                    var column = ParseColumnDefinition(ref lexer);
                    columns.Add(column);
                    constraints.AddRange(column.Constraints);
                }

                if (lexer.Current.Type == TokenType.Comma)
                {
                    Advance(ref lexer);
                    if (lexer.Current.Type == TokenType.RightParen)
                    {
                        AddExpectedDiagnostic(ref lexer, "a column or constraint definition after ','");
                    }
                }
                else { break; }
            }

            ExpectDdlToken(ref lexer, TokenType.RightParen, "')'");
        }

        foreach (var column in columns)
        {
            if (column.IsExplicitlyNullable && !column.IsPrimaryKey && column.Identity is null &&
                constraints.Any(constraint => constraint.Kind == SqlConstraintKind.PrimaryKey &&
                    constraint.Columns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase)))
            {
                AddSyntaxDiagnostic(pos, _lastTokenEnd, $"Conflicting NULL/NOT NULL declarations for column '{column.ColumnName}'.");
            }
        }

        return new SqlCreateTableExpression(table, columns, ifNotExists, null,
            Location.Create(1, 1, pos, _lastTokenEnd), constraints);
    }

    private SqlColumnDefinition ParseColumnDefinition(ref TokenLexer lexer)
    {
        int columnStart = lexer.Current.Position;
        string columnName = string.Empty;
        if (IsIdentifierOrKeyword(ref lexer))
        {
            columnName = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
        }
        else
        {
            AddExpectedDiagnostic(ref lexer, "a column name");
        }

        string dataType = ParseDefinitionType(ref lexer, $"Column '{columnName}'", out var resolvedType);

        // Parse optional constraints
        bool isNullable = true;
        bool sawNull = false;
        bool sawNotNull = false;
        bool isPrimaryKey = false;
        SqlExpression? defaultValue = null;
        string? collationName = null;
        SqlIdentityDefinition? identity = null;
        var constraints = new List<SqlConstraintDefinition>();

        while (!IsAtEnd(ref lexer) &&
               lexer.Current.Type != TokenType.Comma &&
               lexer.Current.Type != TokenType.RightParen &&
               lexer.Current.Type != TokenType.Semicolon)
        {
            if (IsWord(ref lexer, "GENERATED") || IsWord(ref lexer, "IDENTITY"))
            {
                if (identity is not null) { AddSyntaxDiagnostic(ref lexer, "A column may declare only one IDENTITY clause."); }
                identity = ParseIdentity(ref lexer);
            }
            else if (IsKeyword(ref lexer, "COLLATE"))
            {
                if (collationName is not null)
                {
                    AddSyntaxDiagnostic(ref lexer, "A column may declare only one COLLATE clause.");
                }
                Advance(ref lexer);
                collationName = ParseCollationName(ref lexer);
            }
            else if (IsKeyword(ref lexer, "NOT"))
            {
                Advance(ref lexer);
                if (IsKeyword(ref lexer, "NULL"))
                {
                    isNullable = false;
                    sawNotNull = true;
                    Advance(ref lexer);
                }
                else
                {
                    // A bare NOT used to be skipped, leaving the column nullable.
                    AddExpectedDiagnostic(ref lexer, "NULL after NOT");
                }
            }
            else if (IsKeyword(ref lexer, "NULL"))
            {
                isNullable = true;
                sawNull = true;
                Advance(ref lexer);
            }
            else if (IsConstraintStart(ref lexer))
            {
                var constraint = ParseConstraint(ref lexer, columnName);
                constraints.Add(constraint);
                if (constraint.Kind == SqlConstraintKind.PrimaryKey)
                {
                    isPrimaryKey = true;
                }
            }
            else if (IsKeyword(ref lexer, "DEFAULT"))
            {
                Advance(ref lexer);
                defaultValue = ParseColumnDefault(ref lexer);
            }
            else
            {
                // Recovery leaves an offending token in place, so this may already be
                // reported; either way, skip to the end of this column definition so one
                // mistake yields one diagnostic.
                AddSyntaxDiagnostic(ref lexer, "Expected a column constraint or the end of the column definition.");
                SkipToColumnDefinitionEnd(ref lexer);
            }
        }

        // PostgreSQL parse_utilcmd.c:873: explicit NULL conflicts with every NOT NULL implication.
        if (sawNull && (sawNotNull || isPrimaryKey || identity is not null))
        {
            AddSyntaxDiagnostic(columnStart, _lastTokenEnd, $"Conflicting NULL/NOT NULL declarations for column '{columnName}'.");
        }
        return new SqlColumnDefinition(columnName, dataType, isNullable && identity is null, isPrimaryKey, defaultValue, constraints, collationName, resolvedType, identity, sawNull);
    }

    /// <summary>
    /// Parses the arguments of a parameterized type, <c>(n)</c> or <c>(n, m)</c>, into
    /// their normalized text. Each argument must be an unsigned integer literal. The
    /// MAX means unbounded only for VARCHAR, NVARCHAR and VARBINARY. The
    /// arguments used to be the concatenated text of every token up to <c>)</c>, so
    /// <c>VARCHAR(25 5)</c> became VARCHAR(255), <c>DECIMAL(10, 2, 5)</c> lost its third
    /// argument and <c>VARCHAR(MAX)</c> failed in the planner with a raw format error.
    /// </summary>
    private string ParseTypeArguments(ref TokenLexer lexer, string subject, string typeName)
    {
        Advance(ref lexer); // consume (
        if (IsKeywordOrFunction(ref lexer, "MAX") && SqlTypeModifiers.AllowsMax(typeName))
        {
            Advance(ref lexer);
            if (lexer.Current.Type == TokenType.RightParen)
            {
                Advance(ref lexer);
                return "(MAX)";
            }
            AddTypeModifierDiagnostic(subject, SqlTypeModifiers.ArgumentShape, lexer.Current.Position,
                lexer.Current.Position + lexer.Current.Value.Length);
            SkipToClosingParenthesis(ref lexer);
            return string.Empty;
        }
        if (TryReadCastArgument(ref lexer, out int? first))
        {
            int? second = null;
            bool valid = true;
            if (lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                valid = TryReadCastArgument(ref lexer, out second);
            }

            if (valid && lexer.Current.Type == TokenType.RightParen)
            {
                Advance(ref lexer);
                return second is null
                    ? string.Create(CultureInfo.InvariantCulture, $"({first})")
                    : string.Create(CultureInfo.InvariantCulture, $"({first},{second})");
            }

            if (valid && (IsAtEnd(ref lexer) || lexer.Current.Type == TokenType.Semicolon))
            {
                AddExpectedDiagnostic(ref lexer, "')' after the type arguments");
                return string.Empty;
            }
        }

        AddTypeModifierDiagnostic(subject, SqlTypeModifiers.ArgumentShape, lexer.Current.Position,
            lexer.Current.Position + lexer.Current.Value.Length);
        SkipToClosingParenthesis(ref lexer);
        return string.Empty;
    }

    /// <summary>
    /// Skips to the <c>,</c> or <c>)</c> that ends the current column definition, across
    /// nested parentheses, stopping at <c>;</c>.
    /// </summary>
    private void SkipToColumnDefinitionEnd(ref TokenLexer lexer)
    {
        int depth = 0;
        while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon)
        {
            if (depth == 0 && lexer.Current.Type is TokenType.Comma or TokenType.RightParen)
            {
                return;
            }

            if (lexer.Current.Type == TokenType.LeftParen)
            {
                depth++;
            }
            else if (lexer.Current.Type == TokenType.RightParen)
            {
                depth--;
            }

            Advance(ref lexer);
        }
    }

    /// <summary>
    /// Parses one supported ALTER TABLE action. Unknown or incomplete actions are rejected here,
    /// naming it, and yields a bare <see cref="SqlQueryCommandType.Alter"/> expression
    /// rather than an action node: a stub action once reached the catalog as a DROP of
    /// column <c>?</c> (#1068).
    /// </summary>
    private SqlQueryExpression ParseAlterTable(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume ALTER

        // TABLE is required: ALTER INDEX ix RENAME TO iy used to report IX as the
        // unsupported ALTER TABLE action.
        if (!IsKeyword(ref lexer, "TABLE"))
        {
            if (lexer.Current.Type == TokenType.Keyword)
            {
                AddSyntaxDiagnostic(ref lexer,
                    $"ALTER {CurrentText(ref lexer).ToUpperInvariant()} is not supported; the dialect supports ALTER TABLE only.");
            }
            else
            {
                AddExpectedDiagnostic(ref lexer, "TABLE after ALTER");
            }

            ConsumeRemaining(ref lexer);
            return new SqlQueryExpression(SqlQueryCommandType.Alter, null,
                Location.Create(1, 1, pos, _lastTokenEnd));
        }
        Advance(ref lexer);

        // Table reference (no alias). ADD or DROP here means the table name is missing:
        // ALTER TABLE ADD COLUMN c INT used to report COLUMN as the unsupported action.
        var table = IsKeyword(ref lexer, "ADD") || IsKeyword(ref lexer, "DROP")
            ? MissingTableReference(ref lexer)
            : ParseUnaliasedTableReference(ref lexer);

        // Action: ADD/DROP, RENAME, a column facet, or a complete ALTER COLUMN definition.
        SqlAlterAction? action;
        if (TryRejectIdentityAlter(ref lexer))
        {
            return new SqlQueryExpression(SqlQueryCommandType.Alter, null, Location.Create(1, 1, pos, _lastTokenEnd));
        }
        if (IsWord(ref lexer, "RENAME"))
        {
            action = ParseRenameAction(ref lexer);
        }
        else if (IsWord(ref lexer, "ALTER") && !IsAlterColumnDefinition(lexer))
        {
            action = ParseAlterColumnFacet(ref lexer);
        }
        else if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ALTER") && IsAlterColumnDefinition(lexer))
        {
            Advance(ref lexer);
            ExpectKeyword(ref lexer, "COLUMN", "ALTER COLUMN");
            action = new SqlAlterColumnAction(ParseColumnDefinition(ref lexer));
        }
        else if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ADD"))
        {
            Advance(ref lexer);
            if (IsConstraintStart(ref lexer))
            {
                action = new SqlAlterAddConstraintAction(ParseConstraint(ref lexer, null));
            }
            else
            {
                // Optional COLUMN keyword
                if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "COLUMN"))
                {
                    Advance(ref lexer);
                }
                if (!IsIdentifierOrKeyword(ref lexer))
                {
                    AddSyntaxDiagnostic(ref lexer, "Expected a column definition after ALTER TABLE ... ADD.");
                }
                action = new SqlAlterAddColumnAction(ParseColumnDefinition(ref lexer));
            }
        }
        else if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "DROP"))
        {
            Advance(ref lexer);
            bool isConstraint = IsKeyword(ref lexer, "CONSTRAINT");
            if (isConstraint) { Advance(ref lexer); }
            // Optional COLUMN keyword
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "COLUMN"))
            {
                Advance(ref lexer);
            }

            string colName = string.Empty;
            if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                colName = CurrentIdentifierText(ref lexer);
                Advance(ref lexer);
            }
            else
            {
                AddSyntaxDiagnostic(ref lexer, isConstraint
                    ? "Expected a constraint name after ALTER TABLE ... DROP CONSTRAINT."
                    : "Expected a column name after ALTER TABLE ... DROP.");
            }
            action = isConstraint
                ? new SqlAlterDropConstraintAction(colName)
                : new SqlAlterDropColumnAction(colName);
        }
        else
        {
            if (IsWord(ref lexer, "SET") && TryPeekToken(lexer, out string next, out _) && next.Equals("SCHEMA", StringComparison.OrdinalIgnoreCase))
            { RejectAlterForm(ref lexer, "SET SCHEMA"); }
            else { RejectAlterTableAction(ref lexer); }
            ConsumeRemaining(ref lexer);
            return new SqlQueryExpression(SqlQueryCommandType.Alter, null,
                Location.Create(1, 1, pos, _lastTokenEnd));
        }

        if (action is null)
        {
            return new SqlQueryExpression(SqlQueryCommandType.Alter, null, Location.Create(1, 1, pos, _lastTokenEnd));
        }
        if (lexer.Current.Type == TokenType.Comma)
        {
            RejectAlterForm(ref lexer, "comma-separated ALTER TABLE actions");
        }
        return new SqlAlterTableExpression(table, action, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private bool _columnDefault;

    private SqlExpression ParseColumnDefault(ref TokenLexer lexer)
    {
        // PostgreSQL gram.y:4032-4037 uses b_expr so a following NOT NULL is
        // a column constraint. Parentheses and call arguments retain full expressions.
        _columnDefault = true;
        try { return ParseAddition(ref lexer); }
        finally { _columnDefault = false; }
    }

    private SqlAlterAction? ParseRenameAction(ref TokenLexer lexer)
    {
        Advance(ref lexer);
        if (IsWord(ref lexer, "CONSTRAINT"))
        {
            RejectAlterForm(ref lexer, "RENAME CONSTRAINT");
            return null;
        }
        if (IsWord(ref lexer, "TO"))
        {
            Advance(ref lexer);
            string target = ReadAlterName(ref lexer, "table name after RENAME TO");
            if (lexer.Current.Type == TokenType.Dot)
            {
                AddSyntaxDiagnostic(ref lexer, "RENAME TO requires an unqualified target in the same schema.");
                ConsumeRemaining(ref lexer);
            }
            return new SqlAlterRenameTableAction(target);
        }
        if (IsWord(ref lexer, "COLUMN")) { Advance(ref lexer); }
        string column = ReadAlterName(ref lexer, "column name after RENAME");
        ExpectIdentityWord(ref lexer, "TO");
        return new SqlAlterRenameColumnAction(column, ReadAlterName(ref lexer, "column name after TO"));
    }

    private SqlAlterAction? ParseAlterColumnFacet(ref TokenLexer lexer)
    {
        Advance(ref lexer);
        if (IsWord(ref lexer, "CONSTRAINT"))
        {
            RejectAlterTableAction(ref lexer);
            ConsumeRemaining(ref lexer);
            return null;
        }
        if (IsWord(ref lexer, "COLUMN")) { Advance(ref lexer); }
        string column = ReadAlterName(ref lexer, "column name after ALTER");
        bool set = IsWord(ref lexer, "SET");
        bool drop = IsWord(ref lexer, "DROP");
        if (set || drop)
        {
            Advance(ref lexer);
            if (IsWord(ref lexer, "DEFAULT"))
            {
                Advance(ref lexer);
                return set ? new SqlAlterSetDefaultAction(column, ParseColumnDefault(ref lexer)) : new SqlAlterDropDefaultAction(column);
            }
            if (IsWord(ref lexer, "NOT"))
            {
                Advance(ref lexer);
                ExpectKeyword(ref lexer, "NULL", "NOT NULL");
                return set ? new SqlAlterSetNotNullAction(column) : new SqlAlterDropNotNullAction(column);
            }
        }
        if (!set && !drop && IsWord(ref lexer, "TYPE"))
        {
            RejectAlterForm(ref lexer, "ALTER COLUMN TYPE");
        }
        else if (set && IsWord(ref lexer, "DATA") && TryPeekToken(lexer, out string next, out _) && next.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
        {
            RejectAlterForm(ref lexer, "ALTER COLUMN SET DATA TYPE");
        }
        else
        {
            RejectAlterTableAction(ref lexer);
            ConsumeRemaining(ref lexer);
        }
        return null;
    }

    private string ReadAlterName(ref TokenLexer lexer, string subject)
    {
        if (!IsIdentifierOrKeyword(ref lexer))
        {
            AddExpectedDiagnostic(ref lexer, subject);
            return string.Empty;
        }
        string name = CurrentIdentifierText(ref lexer);
        Advance(ref lexer);
        return name;
    }

    private void RejectAlterForm(ref TokenLexer lexer, string action)
    {
        _parseDiagnostics.Add(QueryDiagnostics.UnsupportedClause(action, Profile.Language,
            Location.Create(1, 1, lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length)));
        ConsumeRemaining(ref lexer);
    }

    private static bool IsAlterColumnDefinition(TokenLexer lexer)
    {
        // TYPE/SET DATA TYPE use the capability diagnostic; facets have separate action nodes.
        // This action uses the same full column definition as CREATE and ADD.
        Next(ref lexer);
        if (!IsKeyword(ref lexer, "COLUMN"))
        {
            return false;
        }
        Next(ref lexer);
        if (!IsIdentifierOrKeyword(ref lexer))
        {
            return false;
        }
        Next(ref lexer);
        return CurrentText(ref lexer).ToUpperInvariant() is not ("TYPE" or "SET" or "DROP");

        static void Next(ref TokenLexer tokens)
        {
            while (tokens.MoveNext())
            {
                if (tokens.Current.Type != TokenType.Comment)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Reports an unknown or incomplete ALTER TABLE action with the supported grammar.
    /// </summary>
    private void RejectAlterTableAction(ref TokenLexer lexer)
    {
        const string supported = "ADD [COLUMN], ADD CONSTRAINT, DROP [COLUMN], DROP CONSTRAINT, RENAME TO, RENAME [COLUMN], ALTER COLUMN, and ALTER [COLUMN] SET/DROP DEFAULT or SET/DROP NOT NULL";
        if (IsAtEnd(ref lexer) || lexer.Current.Type == TokenType.Semicolon)
        {
            AddSyntaxDiagnostic(ref lexer, $"ALTER TABLE requires an action: {supported}.");
            return;
        }

        int start = lexer.Current.Position;
        int end = start + lexer.Current.Value.Length;
        string action = CurrentText(ref lexer).ToUpperInvariant();
        if (lexer.Current.Type is TokenType.Identifier or TokenType.Keyword &&
            TryPeekToken(lexer, out string next, out int nextEnd) &&
            next.ToUpperInvariant() is "COLUMN" or "CONSTRAINT" or "TO")
        {
            action = $"{action} {next.ToUpperInvariant()}";
            end = nextEnd;
        }

        AddSyntaxDiagnostic(start, end,
            $"The ALTER TABLE action '{action}' is not supported; the supported actions are {supported}.");
    }

    /// <summary>
    /// Dispatches DROP on its object word, including contextual SCHEMA, before the
    /// historical optional-TABLE fallback.
    /// </summary>
    private SqlQueryExpression ParseDrop(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume DROP

        if (IsKeyword(ref lexer, "VIEW")) { return ParseDropView(ref lexer, pos); }
        if (IsWord(ref lexer, "SCHEMA"))
        {
            return ParseDropSchema(ref lexer, pos);
        }

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "INDEX"))
        {
            return ParseDropIndex(ref lexer, pos);
        }

        if (IsKeyword(ref lexer, "FUNCTION"))
        {
            return ParseDropFunction(ref lexer, pos);
        }
        if (IsKeyword(ref lexer, "DOMAIN"))
        {
            return ParseDropDomain(ref lexer, pos);
        }
        return ParseDropTable(ref lexer, pos);
    }

    private SqlDropIndexExpression ParseDropIndex(ref TokenLexer lexer, int pos)
    {
        Advance(ref lexer); // consume INDEX

        bool ifExists = ParseExistenceGuard(ref lexer, notExists: false);

        // Index name
        string indexName = "?";
        if (IsNameToken(ref lexer))
        {
            indexName = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
        }
        else
        {
            AddExpectedDiagnostic(ref lexer, "an index name");
        }

        // ON <table> — required by the dialect (index names are table-scoped).
        ExpectKeyword(ref lexer, "ON", "ON and the table of the index");

        var table = ParseUnaliasedTableReference(ref lexer);

        return new SqlDropIndexExpression(indexName, table, ifExists, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private SqlDropTableExpression ParseDropTable(ref TokenLexer lexer, int pos)
    {
        // TABLE
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "TABLE"))
        {
            Advance(ref lexer);
        }

        bool ifExists = ParseExistenceGuard(ref lexer, notExists: false);

        var table = ParseUnaliasedTableReference(ref lexer);

        return new SqlDropTableExpression(table, ifExists, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    /// <summary>
    /// Parses an optionally schema-qualified table reference without alias
    /// support (the DDL form). A missing reference is reported and yields the
    /// <c>?</c> placeholder, which never executes because of that error.
    /// </summary>
    private SqlTableReference ParseUnaliasedTableReference(ref TokenLexer lexer)
    {
        if (IsNameToken(ref lexer))
        {
            string firstPart = CurrentIdentifierText(ref lexer);
            string? schemaName = null;

            if (Advance(ref lexer) && lexer.Current.Type == TokenType.Dot)
            {
                if (Advance(ref lexer) && IsIdentifierOrKeyword(ref lexer))
                {
                    schemaName = firstPart;
                    firstPart = CurrentIdentifierText(ref lexer);
                    Advance(ref lexer);
                }
                else
                {
                    AddExpectedDiagnostic(ref lexer, "a table name after '.'");
                }
            }

            return new SqlTableReference(firstPart, schemaName, null);
        }

        return MissingTableReference(ref lexer);
    }

    private SqlTableReference MissingTableReference(ref TokenLexer lexer)
    {
        AddExpectedDiagnostic(ref lexer, "a table name");
        return new SqlTableReference("?", null, null);
    }
}
