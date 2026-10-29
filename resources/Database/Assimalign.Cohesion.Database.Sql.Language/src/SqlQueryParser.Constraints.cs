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
                AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseConstraintVoid);
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
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseConstraintVoid2);
            return new SqlConstraintDefinition(name, SqlConstraintKind.Check, columns);
        }

        ExpectDdlKeyword(ref lexer, "REFERENCES");
        var parent = ParseUnaliasedTableReference(ref lexer);
        var parentColumns = ParseConstraintColumns(ref lexer);
        var onDelete = SqlReferentialAction.NoAction;
        var onUpdate = SqlReferentialAction.NoAction;
        bool hasDelete = false, hasUpdate = false;
        while (IsKeyword(ref lexer, "ON"))
        {
            Advance(ref lexer);
            bool update = IsKeyword(ref lexer, "UPDATE");
            if (!update && !IsKeyword(ref lexer, "DELETE"))
            {
                AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseConstraintVoid6);
                break;
            }
            if (update ? hasUpdate : hasDelete)
            {
                AddSyntaxDiagnostic(ref lexer, ErrorMessages.ReferentialActionRepeated);
            }
            Advance(ref lexer);
            var action = ParseReferentialAction(ref lexer);
            if (update) { onUpdate = action; hasUpdate = true; }
            else { onDelete = action; hasDelete = true; }
        }
        if (columns.Count != parentColumns.Count)
        {
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseConstraintVoid7);
        }
        return new SqlConstraintDefinition(name, SqlConstraintKind.ForeignKey, columns,
            parent, parentColumns, onDelete, onUpdate: onUpdate);
    }

    private SqlReferentialAction ParseReferentialAction(ref TokenLexer lexer)
    {
        if (IsKeyword(ref lexer, "CASCADE"))
        {
            Advance(ref lexer);
            return SqlReferentialAction.Cascade;
        }
        if (IsKeyword(ref lexer, "RESTRICT"))
        {
            Advance(ref lexer);
            return SqlReferentialAction.Restrict;
        }
        if (IsWord(ref lexer, "NO"))
        {
            Advance(ref lexer);
            if (IsWord(ref lexer, "ACTION")) { Advance(ref lexer); }
            else { AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseConstraintVoid3); }
            return SqlReferentialAction.NoAction;
        }
        if (IsKeyword(ref lexer, "SET"))
        {
            Advance(ref lexer);
            bool setNull = IsKeyword(ref lexer, "NULL");
            ExpectDdlKeyword(ref lexer, setNull ? "NULL" : "DEFAULT");
            if (lexer.Current.Type == TokenType.LeftParen)
            {
                AddUnsupportedSurfaceDiagnostic(lexer.Current.Position, lexer.Current.Position + 1,
                    ErrorMessages.SqlQueryParserParseConstraintVoid4);
                ParseConstraintColumns(ref lexer);
            }
            return setNull ? SqlReferentialAction.SetNull : SqlReferentialAction.SetDefault;
        }
        AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseConstraintVoid5);
        return SqlReferentialAction.NoAction;
    }

    private List<string> ParseConstraintColumns(ref TokenLexer lexer)
    {
        var columns = new List<string>();
        if (!ExpectDdlToken(ref lexer, TokenType.LeftParen, "'('")) { return columns; }
        do
        {
            if (!IsIdentifierOrKeyword(ref lexer))
            {
                AddSyntaxDiagnostic(ref lexer, ErrorMessages.QueryParserParseConstraintColumnsVoid);
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
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserReadDdlIdentifierVoid);
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
        AddSyntaxDiagnostic(ref lexer, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserExpectDdlKeywordVoid, keyword));
        return false;
    }

    private bool ExpectDdlToken(ref TokenLexer lexer, TokenType token, string description)
    {
        if (lexer.Current.Type == token)
        {
            Advance(ref lexer);
            return true;
        }
        AddSyntaxDiagnostic(ref lexer, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserExpectDdlKeywordVoid, description));
        return false;
    }
}
