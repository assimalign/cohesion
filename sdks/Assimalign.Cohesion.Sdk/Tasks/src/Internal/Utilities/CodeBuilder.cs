using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Sdk.Tasks.Internal;

internal class CodeBuilder
{
    private readonly StringBuilder _builder;

    private int _indentLevel = 0;
    private StatementType _statementType;

    public CodeBuilder()
    {
        _builder = new StringBuilder();
    }


    public CodeBuilder AppendUsing(string @namespace)
    {
        if (_statementType > StatementType.Using)
        {
            throw new InvalidOperationException("");
        }
        _builder.AppendLine($"using {@namespace};");
        _statementType = StatementType.Using;
        return this;
    }


    public CodeBuilder AppendNamespace(string @namespace)
    {
        if (_statementType >= StatementType.Namespace)
        {
            throw new InvalidOperationException("");
        }
        _builder.AppendLine($"namespace {@namespace}");

        _statementType = StatementType.Namespace;
        return this;
    }







    enum StatementType
    {
        None = 0,
        Using = 1,
        Namespace = 2,
    }
}
