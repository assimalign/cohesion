using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlDeclaredScript")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDeclaredScript.DebugView))]
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
                throw new DatabaseException(ErrorMessages.SqlDeclaredScriptDatabase);
            }
            statements.Add(request);
        }
    }
    internal string ObjectKey(int position, Func<string, int, SqlType> resolveType) => Statements[position].Statement.SqlExpression switch
    {
        SqlCreateTableExpression table => SqlObjectIdentity.Table(table.Table.SchemaName ?? SqlPlanner.DefaultSchema, table.Table.TableName),
        SqlCreateViewExpression view => SqlObjectIdentity.View(view.View.SchemaName ?? SqlPlanner.DefaultSchema, view.View.TableName),
        SqlDropViewExpression view => SqlObjectIdentity.View(view.View.SchemaName ?? SqlPlanner.DefaultSchema, view.View.TableName),
        SqlCreateSchemaExpression schema => SqlObjectIdentity.Schema(schema.Name),
        SqlAlterTableExpression table => SqlObjectIdentity.Table(table.Table.SchemaName ?? SqlPlanner.DefaultSchema, table.Table.TableName),
        SqlDropTableExpression table => SqlObjectIdentity.Table(table.Table.SchemaName ?? SqlPlanner.DefaultSchema, table.Table.TableName),
        SqlTruncateExpression truncate => SqlObjectIdentity.Table(truncate.Tables[0].Table.SchemaName ?? SqlPlanner.DefaultSchema, truncate.Tables[0].Table.TableName),
        SqlDropSchemaExpression schema => SqlObjectIdentity.Schema(schema.Name),
        SqlCreateIndexExpression index => SqlObjectIdentity.Index(index.Table.SchemaName ?? SqlPlanner.DefaultSchema, index.Table.TableName, index.IndexName),
        SqlDropIndexExpression index => SqlObjectIdentity.Index(index.Table.SchemaName ?? SqlPlanner.DefaultSchema, index.Table.TableName, index.IndexName),
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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDeclaredScript value)
        {
            _ = value;
        }
    }
}
