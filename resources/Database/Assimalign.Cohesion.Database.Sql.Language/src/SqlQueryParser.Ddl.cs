using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlCreateTableExpression ParseCreateTable(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume CREATE

        // TABLE
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "TABLE"))
        {
            Advance(ref lexer);
        }

        // IF NOT EXISTS
        bool ifNotExists = false;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "IF"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "NOT"))
            {
                Advance(ref lexer);
            }

            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "EXISTS"))
            {
                ifNotExists = true;
                Advance(ref lexer);
            }
        }

        // Table reference
        SqlTableReference? table = null;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            // Don't parse alias for CREATE TABLE
            string firstPart = CurrentText(ref lexer);
            string? schemaName = null;

            if (Advance(ref lexer) && lexer.Current.Type == TokenType.Dot)
            {
                if (Advance(ref lexer) && IsIdentifierOrKeyword(ref lexer))
                {
                    schemaName = firstPart;
                    firstPart = CurrentText(ref lexer);
                    Advance(ref lexer);
                }
            }

            table = new SqlTableReference(firstPart, schemaName, null);
        }
        table ??= new SqlTableReference("?", null, null);

        // Column definitions: ( col1 TYPE, col2 TYPE, ... )
        var columns = new List<SqlColumnDefinition>();
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);

            if (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.RightParen)
            {
                columns.Add(ParseColumnDefinition(ref lexer));

                while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
                {
                    Advance(ref lexer);
                    columns.Add(ParseColumnDefinition(ref lexer));
                }
            }

            if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
            {
                Advance(ref lexer);
            }
        }

        return new SqlCreateTableExpression(table, columns, ifNotExists, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private SqlColumnDefinition ParseColumnDefinition(ref TokenLexer lexer)
    {
        string columnName = string.Empty;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            columnName = CurrentText(ref lexer);
            Advance(ref lexer);
        }

        // Data type (consume one or more tokens until we hit a constraint keyword, comma, or paren)
        string dataType = string.Empty;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            dataType = CurrentText(ref lexer);
            Advance(ref lexer);

            // Handle parameterized types: VARCHAR(100), DECIMAL(18, 4)
            if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
            {
                dataType += "(";
                Advance(ref lexer);

                while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.RightParen)
                {
                    dataType += CurrentText(ref lexer);
                    Advance(ref lexer);
                }

                if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
                {
                    dataType += ")";
                    Advance(ref lexer);
                }
            }
        }

        // Parse optional constraints
        bool isNullable = true;
        bool isPrimaryKey = false;
        SqlExpression? defaultValue = null;

        while (!IsAtEnd(ref lexer) &&
               lexer.Current.Type != TokenType.Comma &&
               lexer.Current.Type != TokenType.RightParen &&
               lexer.Current.Type != TokenType.Semicolon)
        {
            if (IsKeyword(ref lexer, "NOT"))
            {
                Advance(ref lexer);
                if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "NULL"))
                {
                    isNullable = false;
                    Advance(ref lexer);
                }
            }
            else if (IsKeyword(ref lexer, "NULL"))
            {
                isNullable = true;
                Advance(ref lexer);
            }
            else if (IsKeyword(ref lexer, "PRIMARY"))
            {
                Advance(ref lexer);
                if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "KEY"))
                {
                    isPrimaryKey = true;
                    Advance(ref lexer);
                }
            }
            else if (IsKeyword(ref lexer, "DEFAULT"))
            {
                Advance(ref lexer);
                defaultValue = ParsePrimary(ref lexer);
            }
            else
            {
                // Unknown constraint token, skip
                Advance(ref lexer);
            }
        }

        return new SqlColumnDefinition(columnName, dataType, isNullable, isPrimaryKey, defaultValue);
    }

    private SqlAlterTableExpression ParseAlterTable(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume ALTER

        // TABLE
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "TABLE"))
        {
            Advance(ref lexer);
        }

        // Table reference (no alias)
        SqlTableReference? table = null;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            string firstPart = CurrentText(ref lexer);
            string? schemaName = null;

            if (Advance(ref lexer) && lexer.Current.Type == TokenType.Dot)
            {
                if (Advance(ref lexer) && IsIdentifierOrKeyword(ref lexer))
                {
                    schemaName = firstPart;
                    firstPart = CurrentText(ref lexer);
                    Advance(ref lexer);
                }
            }

            table = new SqlTableReference(firstPart, schemaName, null);
        }
        table ??= new SqlTableReference("?", null, null);

        // Action: ADD or DROP
        SqlAlterAction action;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ADD"))
        {
            Advance(ref lexer);
            // Optional COLUMN keyword
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "COLUMN"))
            {
                Advance(ref lexer);
            }

            var colDef = ParseColumnDefinition(ref lexer);
            action = new SqlAlterAddColumnAction(colDef);
        }
        else if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "DROP"))
        {
            Advance(ref lexer);
            // Optional COLUMN keyword
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "COLUMN"))
            {
                Advance(ref lexer);
            }

            string colName = string.Empty;
            if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                colName = CurrentText(ref lexer);
                Advance(ref lexer);
            }
            action = new SqlAlterDropColumnAction(colName);
        }
        else
        {
            // Unknown action, create a stub
            action = new SqlAlterDropColumnAction("?");
            ConsumeRemaining(ref lexer);
        }

        return new SqlAlterTableExpression(table, action, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private SqlDropTableExpression ParseDropTable(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume DROP

        // TABLE
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "TABLE"))
        {
            Advance(ref lexer);
        }

        // IF EXISTS
        bool ifExists = false;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "IF"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "EXISTS"))
            {
                ifExists = true;
                Advance(ref lexer);
            }
        }

        // Table reference (no alias)
        SqlTableReference? table = null;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            string firstPart = CurrentText(ref lexer);
            string? schemaName = null;

            if (Advance(ref lexer) && lexer.Current.Type == TokenType.Dot)
            {
                if (Advance(ref lexer) && IsIdentifierOrKeyword(ref lexer))
                {
                    schemaName = firstPart;
                    firstPart = CurrentText(ref lexer);
                    Advance(ref lexer);
                }
            }

            table = new SqlTableReference(firstPart, schemaName, null);
        }
        table ??= new SqlTableReference("?", null, null);

        return new SqlDropTableExpression(table, ifExists, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }
}
