using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    /// <summary>
    /// Dispatches a CREATE statement on the object keyword after CREATE: INDEX
    /// (optionally preceded by UNIQUE) parses as CREATE INDEX, everything else as
    /// CREATE TABLE (the TABLE keyword itself stays optional-tolerant).
    /// </summary>
    private SqlQueryExpression ParseCreate(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume CREATE

        if (!IsAtEnd(ref lexer) && (IsKeyword(ref lexer, "UNIQUE") || IsKeyword(ref lexer, "INDEX")))
        {
            return ParseCreateIndex(ref lexer, pos);
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

        return new SqlCreateTableExpression(table, columns, ifNotExists, null,
            Location.Create(1, 1, pos, _lastTokenEnd), constraints);
    }

    private SqlColumnDefinition ParseColumnDefinition(ref TokenLexer lexer)
    {
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

        string dataType = string.Empty;
        if (IsIdentifierOrKeyword(ref lexer))
        {
            dataType = CurrentText(ref lexer);
            Advance(ref lexer);

            // Handle parameterized types: VARCHAR(100), DECIMAL(18, 4)
            if (lexer.Current.Type == TokenType.LeftParen)
            {
                dataType += ParseTypeArguments(ref lexer);
            }
        }
        else
        {
            AddExpectedDiagnostic(ref lexer, $"a data type for column '{columnName}'");
        }

        // Parse optional constraints
        bool isNullable = true;
        bool isPrimaryKey = false;
        SqlExpression? defaultValue = null;
        string? collationName = null;
        var constraints = new List<SqlConstraintDefinition>();

        while (!IsAtEnd(ref lexer) &&
               lexer.Current.Type != TokenType.Comma &&
               lexer.Current.Type != TokenType.RightParen &&
               lexer.Current.Type != TokenType.Semicolon)
        {
            if (IsKeyword(ref lexer, "COLLATE"))
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
                defaultValue = ParsePrimary(ref lexer);
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

        return new SqlColumnDefinition(columnName, dataType, isNullable, isPrimaryKey, defaultValue, constraints, collationName);
    }

    /// <summary>
    /// Parses the arguments of a parameterized type, <c>(n)</c> or <c>(n, m)</c>, into
    /// their normalized text. Each argument must be an unsigned integer literal. The
    /// arguments used to be the concatenated text of every token up to <c>)</c>, so
    /// <c>VARCHAR(25 5)</c> became VARCHAR(255), <c>DECIMAL(10, 2, 5)</c> lost its third
    /// argument and <c>VARCHAR(MAX)</c> failed in the planner with a raw format error.
    /// </summary>
    private string ParseTypeArguments(ref TokenLexer lexer)
    {
        Advance(ref lexer); // consume (
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

        AddSyntaxDiagnostic(ref lexer,
            "Type arguments must be one or two unsigned integer literals within the Int32 range, as in VARCHAR(100) or DECIMAL(18, 4).");
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
    /// Parses ALTER TABLE with one ADD or DROP action. Any other action is rejected here,
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

        // Action: ADD or DROP
        SqlAlterAction action;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ADD"))
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
            RejectAlterTableAction(ref lexer);
            ConsumeRemaining(ref lexer);
            return new SqlQueryExpression(SqlQueryCommandType.Alter, null,
                Location.Create(1, 1, pos, _lastTokenEnd));
        }

        return new SqlAlterTableExpression(table, action, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    /// <summary>
    /// Reports an ALTER TABLE action outside ADD and DROP, naming it: <c>RENAME TO</c>,
    /// <c>ALTER COLUMN</c>, <c>MODIFY</c> and the like.
    /// </summary>
    private void RejectAlterTableAction(ref TokenLexer lexer)
    {
        const string supported = "ADD [COLUMN], ADD CONSTRAINT, DROP [COLUMN] and DROP CONSTRAINT";
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
    /// Dispatches a DROP statement on the object keyword after DROP: INDEX parses
    /// as DROP INDEX, everything else as DROP TABLE.
    /// </summary>
    private SqlQueryExpression ParseDrop(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume DROP

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "INDEX"))
        {
            return ParseDropIndex(ref lexer, pos);
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
