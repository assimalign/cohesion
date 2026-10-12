using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed class SqlDeclaredScript
{
    internal SqlDeclaredScript(string name, string sql, SqlQueryParserOptions options)
    {
        Name = name;
        ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        var statements = new List<SqlQueryRequest>();
        var lexer = new TokenLexer(sql, SqlLanguageProfile.Instance.ToLexerOptions());
        int start = 0;
        bool content = false;
        while (lexer.MoveNext())
        {
            var token = lexer.Current;
            if (token.Type == TokenType.Semicolon)
            {
                if (content) { Add(sql[start..token.Position]); }
                start = token.Position + 1;
                content = false;
            }
            else if (token.Type != TokenType.Comment) { content = true; }
        }
        if (content) { Add(sql[start..]); }
        Statements = statements.AsReadOnly();
        void Add(string text)
        {
            var request = SqlQueryRequest.FromSql(text, null, options);
            if (request.Statement.SqlExpression is SqlTransactionExpression)
            {
                throw new DatabaseException("COHSQLS003: Declared scripts own statement transactions; transaction-control statements are not allowed.");
            }
            statements.Add(request);
        }
    }
    internal string ObjectKey(int position, Func<string, int, SqlType> resolveType) => Statements[position].Statement.SqlExpression switch
    {
        SqlCreateTableExpression table => SqlObjectIdentity.Table(table.Table.SchemaName ?? "dbo", table.Table.TableName),
        SqlAlterTableExpression table => SqlObjectIdentity.Table(table.Table.SchemaName ?? "dbo", table.Table.TableName),
        SqlDropTableExpression table => SqlObjectIdentity.Table(table.Table.SchemaName ?? "dbo", table.Table.TableName),
        SqlCreateIndexExpression index => SqlObjectIdentity.Index(index.Table.SchemaName ?? "dbo", index.Table.TableName, index.IndexName),
        SqlDropIndexExpression index => SqlObjectIdentity.Index(index.Table.SchemaName ?? "dbo", index.Table.TableName, index.IndexName),
        SqlCreateFunctionExpression function => "F:" + SqlDefinitionBinder.Signature(function.Name, function.Parameters.Select((parameter, index) => resolveType(parameter.DataType, index))),
        SqlDropFunctionExpression function => "F:" + SqlDefinitionBinder.Signature(function.Name, function.ParameterTypes.Select(resolveType)),
        SqlCreateDomainExpression domain => SqlDefinitionBinder.DomainKey(domain.Name),
        SqlDropDomainExpression domain => SqlDefinitionBinder.DomainKey(domain.Name),
        _ => string.Empty,
    };
    internal string Name { get; }
    internal string ContentHash { get; }
    internal IReadOnlyList<SqlQueryRequest> Statements { get; }
    internal static string Hash(IReadOnlyList<SqlDeclaredScript> scripts)
    {
        var text = new StringBuilder();
        foreach (var script in scripts) { text.Append(script.Name.Length).Append(':').Append(script.Name).Append(script.ContentHash); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
