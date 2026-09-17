using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Assimalign.Cohesion.Sdk.Database.Tasks.Compilation;

/// <summary>
/// Mirrors DatabaseSchemaCompiler's deliberately narrow expression grammar without loading or
/// executing consumer code.
/// </summary>
internal sealed class CSharpExpressionCanonicalizer
{
    private static readonly SymbolDisplayFormat QualifiedTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private readonly SemanticModel _model;
    private readonly StringBuilder _builder = new();
    private readonly Dictionary<IParameterSymbol, string> _parameters = new(SymbolEqualityComparer.Default);

    private CSharpExpressionCanonicalizer(SemanticModel model)
    {
        _model = model;
    }

    internal static string Canonicalize(SemanticModel model, LambdaExpressionSyntax expression)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(expression);
        var writer = new CSharpExpressionCanonicalizer(model);
        writer.WriteLambda(expression);
        return writer._builder.ToString();
    }

    private void WriteLambda(LambdaExpressionSyntax lambda)
    {
        IMethodSymbol invoke = DelegateInvoke(lambda) ?? throw Unsupported(lambda);
        _builder.Append("lambda<").Append(CSharpTypeIdentity.Create(invoke.ReturnType)).Append(">(");
        IReadOnlyList<ParameterSyntax> parameters = Parameters(lambda);
        for (int index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
            {
                _builder.Append(',');
            }

            IParameterSymbol symbol = _model.GetDeclaredSymbol(parameters[index]) ?? throw Unsupported(parameters[index]);
            string parameterId = $"p{_parameters.Count}";
            _parameters[symbol] = parameterId;
            _builder.Append(parameterId).Append(':').Append(CSharpTypeIdentity.Create(symbol.Type));
        }
        _builder.Append(")->");

        if (lambda.Body is not ExpressionSyntax body)
        {
            throw Unsupported(lambda.Body);
        }
        Write(body, checkedContext: false);
    }

    private void Write(ExpressionSyntax expression, bool checkedContext)
    {
        expression = UnwrapParentheses(expression);
        if (expression is CheckedExpressionSyntax checkedExpression)
        {
            Write(checkedExpression.Expression, checkedExpression.Keyword.IsKind(SyntaxKind.CheckedKeyword));
            return;
        }
        if (expression is LambdaExpressionSyntax lambda)
        {
            ITypeSymbol quoteType = _model.GetTypeInfo(lambda).ConvertedType ?? throw Unsupported(lambda);
            _builder.Append("Quote<").Append(CSharpTypeIdentity.Create(quoteType)).Append(">[method=none](");
            WriteLambda(lambda);
            _builder.Append(')');
            return;
        }
        if (TryWriteConstant(expression))
        {
            return;
        }
        if (NeedsImplicitConversion(expression))
        {
            WriteConversion(expression, checkedContext, isExplicitCast: false);
            return;
        }

        WriteCore(expression, checkedContext);
    }

    private void WriteCore(ExpressionSyntax expression, bool checkedContext)
    {
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                WriteIdentifier(identifier);
                break;
            case MemberAccessExpressionSyntax member:
                WriteMember(member);
                break;
            case PrefixUnaryExpressionSyntax unary:
                WriteUnary(unary, checkedContext);
                break;
            case CastExpressionSyntax conversion:
                WriteConversion(conversion.Expression, checkedContext, isExplicitCast: true, conversion);
                break;
            case BinaryExpressionSyntax binary:
                WriteBinary(binary, checkedContext);
                break;
            case ElementAccessExpressionSyntax element:
                WriteArrayIndex(element, checkedContext);
                break;
            case InvocationExpressionSyntax call:
                WriteCall(call, checkedContext);
                break;
            case ConditionalExpressionSyntax conditional:
                _builder.Append("conditional(");
                Write(conditional.Condition, checkedContext);
                _builder.Append(',');
                Write(conditional.WhenTrue, checkedContext);
                _builder.Append(',');
                Write(conditional.WhenFalse, checkedContext);
                _builder.Append(')');
                break;
            default:
                throw Unsupported(expression);
        }
    }

    private void WriteIdentifier(IdentifierNameSyntax identifier)
    {
        if (_model.GetSymbolInfo(identifier).Symbol is IParameterSymbol parameter &&
            _parameters.TryGetValue(parameter, out string? id))
        {
            _builder.Append(id);
            return;
        }
        throw Unsupported(identifier);
    }

    private void WriteMember(MemberAccessExpressionSyntax member)
    {
        ISymbol symbol = _model.GetSymbolInfo(member).Symbol ?? throw Unsupported(member);
        ITypeSymbol memberType;
        bool isStatic;
        switch (symbol)
        {
            case IFieldSymbol field:
                memberType = field.Type;
                isStatic = field.IsStatic;
                break;
            case IPropertySymbol property:
                memberType = property.Type;
                isStatic = property.IsStatic;
                break;
            default:
                throw Unsupported(member);
        }
        if (isStatic)
        {
            throw Unsupported(member);
        }

        _builder.Append("member<").Append(CSharpTypeIdentity.Create(memberType)).Append(">(")
            .Append(CSharpTypeIdentity.Create(symbol.ContainingType))
            .Append('.').Append(symbol.Name).Append(',');
        Write(member.Expression, checkedContext: false);
        _builder.Append(')');
    }

    private void WriteUnary(PrefixUnaryExpressionSyntax unary, bool checkedContext)
    {
        string nodeType = unary.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression when !checkedContext => "Negate",
            SyntaxKind.LogicalNotExpression or SyntaxKind.BitwiseNotExpression => "Not",
            _ => throw Unsupported(unary)
        };
        IUnaryOperation? operation = _model.GetOperation(unary) as IUnaryOperation;
        IMethodSymbol? method = operation?.OperatorMethod;
        EnsureAllowed(method, unary);
        ITypeSymbol resultType = _model.GetTypeInfo(unary).Type ?? throw Unsupported(unary);

        _builder.Append(nodeType).Append('<').Append(CSharpTypeIdentity.Create(resultType))
            .Append(">[method=").Append(MethodId(method)).Append("](");
        Write(unary.Operand, checkedContext);
        _builder.Append(')');
    }

    private void WriteConversion(
        ExpressionSyntax operand,
        bool checkedContext,
        bool isExplicitCast,
        CastExpressionSyntax? cast = null)
    {
        if (checkedContext)
        {
            // ConvertChecked is outside the runtime canonicalizer's supported node set.
            throw Unsupported(cast ?? operand);
        }

        ITypeSymbol resultType = cast is null
            ? _model.GetTypeInfo(operand).ConvertedType ?? throw Unsupported(operand)
            : _model.GetTypeInfo(cast).Type ?? throw Unsupported(cast);
        IMethodSymbol? method = cast is null
            ? null
            : (_model.GetOperation(cast) as IConversionOperation)?.OperatorMethod;
        if (isExplicitCast)
        {
            EnsureAllowed(method, cast ?? operand);
        }

        _builder.Append("Convert<").Append(CSharpTypeIdentity.Create(resultType))
            .Append(">[method=").Append(MethodId(method)).Append("](");
        WriteCore(UnwrapParentheses(operand), checkedContext: false);
        _builder.Append(')');
    }

    private void WriteBinary(BinaryExpressionSyntax binary, bool checkedContext)
    {
        string nodeType = binary.Kind() switch
        {
            SyntaxKind.AddExpression when checkedContext => "AddChecked",
            SyntaxKind.SubtractExpression when checkedContext => "SubtractChecked",
            SyntaxKind.MultiplyExpression when checkedContext => "MultiplyChecked",
            SyntaxKind.AddExpression => "Add",
            SyntaxKind.SubtractExpression => "Subtract",
            SyntaxKind.MultiplyExpression => "Multiply",
            SyntaxKind.DivideExpression => "Divide",
            SyntaxKind.ModuloExpression => "Modulo",
            SyntaxKind.LeftShiftExpression => "LeftShift",
            SyntaxKind.RightShiftExpression => "RightShift",
            SyntaxKind.LogicalAndExpression => "AndAlso",
            SyntaxKind.LogicalOrExpression => "OrElse",
            SyntaxKind.BitwiseAndExpression => "And",
            SyntaxKind.BitwiseOrExpression => "Or",
            SyntaxKind.ExclusiveOrExpression => "ExclusiveOr",
            SyntaxKind.EqualsExpression => "Equal",
            SyntaxKind.NotEqualsExpression => "NotEqual",
            SyntaxKind.LessThanExpression => "LessThan",
            SyntaxKind.LessThanOrEqualExpression => "LessThanOrEqual",
            SyntaxKind.GreaterThanExpression => "GreaterThan",
            SyntaxKind.GreaterThanOrEqualExpression => "GreaterThanOrEqual",
            SyntaxKind.CoalesceExpression => "Coalesce",
            _ => throw Unsupported(binary)
        };

        IBinaryOperation? operation = _model.GetOperation(binary) as IBinaryOperation;
        ITypeSymbol resultType = _model.GetTypeInfo(binary).Type ?? throw Unsupported(binary);
        IMethodSymbol? method = operation?.OperatorMethod;
        bool usesObjectStringConcat = false;
        if (method is null && binary.IsKind(SyntaxKind.AddExpression) &&
            resultType.SpecialType == SpecialType.System_String)
        {
            method = StringConcatMethod(binary);
            usesObjectStringConcat = method.Parameters.All(parameter =>
                parameter.Type.SpecialType == SpecialType.System_Object);
        }
        EnsureAllowed(method, binary);
        bool lifted = operation?.IsLifted == true;
        bool liftedToNull = lifted && IsNullableType(resultType);
        _builder.Append(nodeType).Append('<').Append(CSharpTypeIdentity.Create(resultType))
            .Append(">[method=").Append(MethodId(method))
            .Append(";lifted=").Append(lifted ? '1' : '0')
            .Append(";liftedToNull=").Append(liftedToNull ? '1' : '0').Append("](");
        WriteBinaryOperand(binary.Left, checkedContext, usesObjectStringConcat);
        _builder.Append(',');
        WriteBinaryOperand(binary.Right, checkedContext, usesObjectStringConcat);
        _builder.Append(')');
    }

    private void WriteBinaryOperand(
        ExpressionSyntax operand,
        bool checkedContext,
        bool boxNonStringForConcat)
    {
        ITypeSymbol? operandType = _model.GetTypeInfo(operand).Type;
        if (!boxNonStringForConcat || operandType?.SpecialType == SpecialType.System_String)
        {
            Write(operand, checkedContext);
            return;
        }

        if (_model.GetTypeInfo(operand).ConvertedType?.SpecialType == SpecialType.System_Object &&
            NeedsImplicitConversion(operand))
        {
            Write(operand, checkedContext);
            return;
        }

        ITypeSymbol objectType = _model.Compilation.GetSpecialType(SpecialType.System_Object);
        _builder.Append("Convert<").Append(CSharpTypeIdentity.Create(objectType))
            .Append(">[method=none](");
        Write(operand, checkedContext);
        _builder.Append(')');
    }

    private void WriteArrayIndex(ElementAccessExpressionSyntax element, bool checkedContext)
    {
        if (_model.GetTypeInfo(element.Expression).Type is not IArrayTypeSymbol ||
            element.ArgumentList.Arguments.Count != 1)
        {
            throw Unsupported(element);
        }

        ITypeSymbol resultType = _model.GetTypeInfo(element).Type ?? throw Unsupported(element);
        _builder.Append("ArrayIndex<").Append(CSharpTypeIdentity.Create(resultType))
            .Append(">[method=none;lifted=0;liftedToNull=0](");
        Write(element.Expression, checkedContext);
        _builder.Append(',');
        Write(element.ArgumentList.Arguments[0].Expression, checkedContext);
        _builder.Append(')');
    }

    private IMethodSymbol StringConcatMethod(BinaryExpressionSyntax binary)
    {
        TypeInfo left = _model.GetTypeInfo(binary.Left);
        TypeInfo right = _model.GetTypeInfo(binary.Right);
        bool bothStrings = left.Type?.SpecialType == SpecialType.System_String &&
            right.Type?.SpecialType == SpecialType.System_String;
        SpecialType parameterType = bothStrings
            ? SpecialType.System_String
            : SpecialType.System_Object;

        return _model.Compilation.GetSpecialType(SpecialType.System_String)
            .GetMembers("Concat")
            .OfType<IMethodSymbol>()
            .Single(method =>
                method.IsStatic &&
                method.Parameters.Length == 2 &&
                method.Parameters.All(parameter => parameter.Type.SpecialType == parameterType));
    }

    private void WriteCall(InvocationExpressionSyntax call, bool checkedContext)
    {
        IMethodSymbol method = _model.GetSymbolInfo(call).Symbol as IMethodSymbol ?? throw Unsupported(call);
        IMethodSymbol emitted = method.ReducedFrom ?? method;
        EnsureAllowed(emitted, call);
        _builder.Append("call(").Append(MethodId(emitted)).Append(',');

        ExpressionSyntax? receiver = call.Expression is MemberAccessExpressionSyntax access && !method.IsStatic
            ? access.Expression
            : null;
        if (receiver is null)
        {
            _builder.Append("static");
        }
        else
        {
            Write(receiver, checkedContext);
        }
        if (method.ReducedFrom is not null && call.Expression is MemberAccessExpressionSyntax reducedAccess)
        {
            _builder.Append(',');
            Write(reducedAccess.Expression, checkedContext);
        }
        foreach (ArgumentSyntax argument in ArgumentsInParameterOrder(call.ArgumentList.Arguments, method))
        {
            _builder.Append(',');
            Write(argument.Expression, checkedContext);
        }
        _builder.Append(')');
    }

    private bool TryWriteConstant(ExpressionSyntax expression)
    {
        Optional<object?> optional = _model.GetConstantValue(expression);
        if (!optional.HasValue)
        {
            return false;
        }

        ITypeSymbol type = _model.GetTypeInfo(expression).ConvertedType
            ?? _model.GetTypeInfo(expression).Type
            ?? throw Unsupported(expression);
        object? value = ConvertConstant(optional.Value, type);
        if (value is null)
        {
            _builder.Append("null:").Append(CSharpTypeIdentity.Create(type));
        }
        else if (value is string text)
        {
            _builder.Append("string:").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
        }
        else if (value is char character)
        {
            _builder.Append("char:").Append(((int)character).ToString(CultureInfo.InvariantCulture));
        }
        else if (value is bool boolean)
        {
            _builder.Append(boolean ? "bool:true" : "bool:false");
        }
        else if (type.TypeKind == TypeKind.Enum)
        {
            _builder.Append("enum:").Append(CSharpTypeIdentity.Create(type)).Append(':')
                .Append(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
        else if (value is IFormattable formattable &&
            type.ToDisplayString(QualifiedTypeFormat) is not "System.DateTime" and not "System.DateTimeOffset")
        {
            _builder.Append("value:").Append(CSharpTypeIdentity.Create(type)).Append(':')
                .Append(formattable.ToString(null, CultureInfo.InvariantCulture));
        }
        else
        {
            throw Unsupported(expression);
        }
        return true;
    }

    private bool NeedsImplicitConversion(ExpressionSyntax expression)
    {
        if (expression is CastExpressionSyntax)
        {
            return false;
        }
        TypeInfo info = _model.GetTypeInfo(expression);
        return info.Type is not null && info.ConvertedType is not null &&
            !SymbolEqualityComparer.Default.Equals(info.Type, info.ConvertedType);
    }

    private void EnsureAllowed(IMethodSymbol? method, SyntaxNode expression)
    {
        if (method is not null && !IsAllowedMethod(method))
        {
            throw Unsupported(expression);
        }
    }

    private static bool IsAllowedMethod(IMethodSymbol method)
    {
        string typeName = method.ContainingType.ToDisplayString(QualifiedTypeFormat);
        return typeName is
            "Assimalign.Cohesion.Database.IDatabaseTriggerContext" or
            "System.String" or "System.Math" or "System.MathF" or "System.Decimal" or "System.Convert";
    }

    private static string MethodId(IMethodSymbol? method)
    {
        if (method is null)
        {
            return "none";
        }

        var builder = new StringBuilder()
            .Append(CSharpTypeIdentity.Create(method.ContainingType))
            .Append('.').Append(method.Name);
        if (method.IsGenericMethod)
        {
            builder.Append('<').AppendJoin(',', method.TypeArguments.Select(CSharpTypeIdentity.Create)).Append('>');
        }
        builder.Append('(')
            .AppendJoin(',', method.Parameters.Select(parameter => CSharpTypeIdentity.Create(parameter.Type, parameter.RefKind)))
            .Append(")->")
            .Append(CSharpTypeIdentity.Create(method.ReturnType, method.ReturnsByRef || method.ReturnsByRefReadonly ? RefKind.Ref : RefKind.None));
        return builder.ToString();
    }

    private IMethodSymbol? DelegateInvoke(LambdaExpressionSyntax lambda)
    {
        ITypeSymbol? type = _model.GetTypeInfo(lambda).ConvertedType;
        if (type is INamedTypeSymbol { Name: "Expression", TypeArguments.Length: 1 } expression)
        {
            type = expression.TypeArguments[0];
        }
        return (type as INamedTypeSymbol)?.DelegateInvokeMethod;
    }

    private static bool IsNullableType(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static object? ConvertConstant(object? value, ITypeSymbol type)
    {
        if (value is null || type.TypeKind == TypeKind.Enum)
        {
            return value;
        }
        return type.SpecialType switch
        {
            SpecialType.System_SByte => Convert.ToSByte(value, CultureInfo.InvariantCulture),
            SpecialType.System_Byte => Convert.ToByte(value, CultureInfo.InvariantCulture),
            SpecialType.System_Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            SpecialType.System_UInt16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            SpecialType.System_Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            SpecialType.System_UInt32 => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            SpecialType.System_Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            SpecialType.System_UInt64 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            SpecialType.System_Single => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            SpecialType.System_Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            SpecialType.System_Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            SpecialType.System_Char => Convert.ToChar(value, CultureInfo.InvariantCulture),
            SpecialType.System_Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            SpecialType.System_String => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static IReadOnlyList<ParameterSyntax> Parameters(LambdaExpressionSyntax lambda)
        => lambda switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter],
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters,
            _ => throw Unsupported(lambda)
        };

    private static IEnumerable<ArgumentSyntax> ArgumentsInParameterOrder(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IMethodSymbol method)
    {
        if (!arguments.Any(static argument => argument.NameColon is not null))
        {
            return arguments;
        }
        return arguments.OrderBy(argument =>
        {
            string? name = argument.NameColon?.Name.Identifier.ValueText;
            return name is null
                ? arguments.IndexOf(argument)
                : method.Parameters.First(parameter => parameter.Name == name).Ordinal;
        });
    }

    private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }
        return expression;
    }

    private static NotSupportedException Unsupported(SyntaxNode expression)
        => new($"Expression syntax '{expression.Kind()}' is not deterministic schema syntax.");
}
