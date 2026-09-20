using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace Assimalign.Cohesion.Sdk.Database.Tasks.Compilation;

/// <summary>Mirrors the SQL schema compiler's portable identity of supplied type metadata for Roslyn symbols.</summary>
internal static class CSharpTypeIdentity
{
    internal static string Create(ITypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type is ITypeParameterSymbol parameter)
        {
            return $"!{parameter.Ordinal}:{parameter.Name}";
        }
        if (type is IArrayTypeSymbol array)
        {
            return $"{Create(array.ElementType)}[{new string(',', array.Rank - 1)}]";
        }
        if (type is IPointerTypeSymbol pointer)
        {
            return $"{Create(pointer.PointedAtType)}*";
        }
        if (type is not INamedTypeSymbol named)
        {
            throw new NotSupportedException($"CLR type '{type.ToDisplayString()}' has no portable metadata identity.");
        }

        INamedTypeSymbol identityType = named.IsGenericType ? named.OriginalDefinition : named;
        string metadataName = FullMetadataName(identityType);
        string assembly = HasRuntimeCoreLibraryIdentity(identityType, metadataName)
            ? "System.Private.CoreLib"
            : identityType.ContainingAssembly?.Identity.Name ?? "unknown";
        if (!named.IsGenericType)
        {
            return $"{assembly}:{metadataName}";
        }

        return $"{assembly}:{metadataName}[{string.Join(';', AllTypeArguments(named).Select(Create))}]";
    }

    internal static string Create(ITypeSymbol type, RefKind refKind)
        => refKind is RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnlyParameter
            ? Create(type) + "&"
            : Create(type);

    private static IEnumerable<ITypeSymbol> AllTypeArguments(INamedTypeSymbol type)
    {
        if (type.ContainingType is not null)
        {
            foreach (ITypeSymbol argument in AllTypeArguments(type.ContainingType))
            {
                yield return argument;
            }
        }
        foreach (ITypeSymbol argument in type.TypeArguments)
        {
            yield return argument;
        }
    }

    private static string FullMetadataName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            names.Push(current.MetadataName);
        }

        string prefix = type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : type.ContainingNamespace.ToDisplayString() + ".";
        return prefix + string.Join("+", names);
    }

    private static bool HasRuntimeCoreLibraryIdentity(INamedTypeSymbol type, string metadataName)
    {
        if (type.SpecialType != SpecialType.None)
        {
            return true;
        }

        string? referenceAssembly = type.ContainingAssembly?.Identity.Name;
        if (referenceAssembly is not ("System.Runtime" or "System.Private.CoreLib"))
        {
            return false;
        }

        return metadataName is
            "System.Guid" or
            "System.DateOnly" or
            "System.TimeOnly" or
            "System.DateTimeOffset" or
            "System.TimeSpan";
    }
}
