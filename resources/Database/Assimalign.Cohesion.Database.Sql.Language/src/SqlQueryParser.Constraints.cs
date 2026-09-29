using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private static bool IsConstraintStart(ref TokenLexer lexer) =>
        IsKeyword(ref lexer, "CONSTRAINT") || IsKeyword(ref lexer, "PRIMARY") ||
        IsKeyword(ref lexer, "FOREIGN") || IsKeyword(ref lexer, "REFERENCES") ||
        IsKeyword(ref lexer, "UNIQUE") || IsKeyword(ref lexer, "CHECK");

    private SqlConstraintDefinition ParseConstraint(ref TokenLexer lexer, string? columnName)
    {
        string? name = null;
        if (IsKeyword(ref lexer, "CONSTRAINT"))
        {
            Advance(ref lexer);
            name = ReadDdlIdentifier(ref lexer);
        }

        IReadOnlyList<string> columns = columnName is null ? [] : [columnName];
        if (IsKeyword(ref lexer, "PRIMARY") || IsKeyword(ref lexer, "UNIQUE"))
        {
            bool primary = IsKeyword(ref lexer, "PRIMARY");
            Advance(ref lexer);
            if (primary) { ExpectDdlKeyword(ref lexer, "KEY"); }
            if (columnName is null) { columns = ParseConstraintColumns(ref lexer); }
            return new SqlConstraintDefinition(name,
                primary ? SqlConstraintKind.PrimaryKey : SqlConstraintKind.Unique, columns);
        }

        if (IsKeyword(ref lexer, "CHECK"))
        {
            Advance(ref lexer);
            ExpectDdlToken(ref lexer, TokenType.LeftParen, "'('");
            if (lexer.Current.Type is TokenType.RightParen or TokenType.Semicolon or TokenType.Eof)
            {
                AddSyntaxDiagnostic(ref lexer, "Expected a CHECK predicate.");
            }
            int start = lexer.Current.Position;
            var expression = ParseExpression(ref lexer);
            int end = lexer.Current.Position;
            string text = start >= 0 && end >= start && end <= _sourceText.Length
                ? _sourceText[start..end].Trim()
                : string.Empty;
            ExpectDdlToken(ref lexer, TokenType.RightParen, "')'");
            return new SqlConstraintDefinition(name, SqlConstraintKind.Check, columns,
                checkExpression: expression, checkExpressionText: text);
        }

        if (IsKeyword(ref lexer, "FOREIGN"))
        {
            Advance(ref lexer);
            ExpectDdlKeyword(ref lexer, "KEY");
            if (columnName is null) { columns = ParseConstraintColumns(ref lexer); }
        }
        else if (!IsKeyword(ref lexer, "REFERENCES"))
        {
            AddSyntaxDiagnostic(ref lexer, "Expected PRIMARY KEY, FOREIGN KEY, REFERENCES, CHECK, or UNIQUE.");
            return new SqlConstraintDefinition(name, SqlConstraintKind.Check, columns);
        }

        ExpectDdlKeyword(ref lexer, "REFERENCES");
        var parent = ParseUnaliasedTableReference(ref lexer);
        var parentColumns = ParseConstraintColumns(ref lexer);
        var action = SqlReferentialAction.Restrict;
        if (IsKeyword(ref lexer, "ON"))
        {
            Advance(ref lexer);
            if (IsKeyword(ref lexer, "DELETE"))
            {
                Advance(ref lexer);
                if (IsKeyword(ref lexer, "CASCADE"))
                {
                    action = SqlReferentialAction.Cascade;
                    Advance(ref lexer);
                }
                else
                {
                    ExpectDdlKeyword(ref lexer, "RESTRICT");
                }
            }
            else if (IsKeyword(ref lexer, "UPDATE"))
            {
                // The preflight scanner emits COHDBL001. Consume this action only
                // for error recovery; it is never an accepted referential action.
                Advance(ref lexer);
                if (IsKeyword(ref lexer, "CASCADE") || IsKeyword(ref lexer, "RESTRICT"))
                {
                    Advance(ref lexer);
                }
            }
            else
            {
                AddSyntaxDiagnostic(ref lexer, "Expected DELETE after ON.");
            }
        }
        if (columns.Count != parentColumns.Count)
        {
            AddSyntaxDiagnostic(ref lexer, "Foreign key and referenced key must contain the same number of columns.");
        }
        return new SqlConstraintDefinition(name, SqlConstraintKind.ForeignKey, columns,
            parent, parentColumns, action);
    }

    private List<string> ParseConstraintColumns(ref TokenLexer lexer)
    {
        var columns = new List<string>();
        if (!ExpectDdlToken(ref lexer, TokenType.LeftParen, "'('")) { return columns; }
        do
        {
            if (!IsIdentifierOrKeyword(ref lexer))
            {
                AddSyntaxDiagnostic(ref lexer, "Expected a constraint column name.");
                break;
            }
            columns.Add(ReadDdlIdentifier(ref lexer));
            if (lexer.Current.Type != TokenType.Comma) { break; }
            Advance(ref lexer);
        }
        while (!IsAtEnd(ref lexer));
        ExpectDdlToken(ref lexer, TokenType.RightParen, "')'");
        return columns;
    }

    private string ReadDdlIdentifier(ref TokenLexer lexer)
    {
        if (!IsIdentifierOrKeyword(ref lexer))
        {
            AddSyntaxDiagnostic(ref lexer, "Expected an identifier.");
            return "?";
        }
        string name = CurrentIdentifierText(ref lexer);
        Advance(ref lexer);
        return name;
    }

    private bool ExpectDdlKeyword(ref TokenLexer lexer, string keyword)
    {
        if (IsKeyword(ref lexer, keyword))
        {
            Advance(ref lexer);
            return true;
        }
        AddSyntaxDiagnostic(ref lexer, $"Expected {keyword}.");
        return false;
    }

    private bool ExpectDdlToken(ref TokenLexer lexer, TokenType token, string description)
    {
        if (lexer.Current.Type == token)
        {
            Advance(ref lexer);
            return true;
        }
        AddSyntaxDiagnostic(ref lexer, $"Expected {description}.");
        return false;
    }
}
