using System;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.SourceGeneration.Web.Internal;

/// <summary>
/// A value-equatable capture of a diagnostic to report, holding a serializable location rather than a
/// <see cref="Location"/> (which is not value-equatable and would defeat pipeline caching).
/// </summary>
internal readonly struct DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    private readonly string _descriptorId;
    private readonly string _filePath;
    private readonly TextSpan _textSpan;
    private readonly LinePositionSpan _lineSpan;
    private readonly EquatableArray<string> _messageArguments;

    private DiagnosticInfo(string descriptorId, string filePath, TextSpan textSpan, LinePositionSpan lineSpan, EquatableArray<string> messageArguments)
    {
        _descriptorId = descriptorId;
        _filePath = filePath;
        _textSpan = textSpan;
        _lineSpan = lineSpan;
        _messageArguments = messageArguments;
    }

    internal string DescriptorId => _descriptorId;

    internal static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params string[] messageArguments)
    {
        FileLinePositionSpan span = location?.GetLineSpan() ?? default;

        return new DiagnosticInfo(
            descriptor.Id,
            location?.SourceTree?.FilePath ?? string.Empty,
            location?.SourceSpan ?? default,
            span.Span,
            new EquatableArray<string>(ImmutableArray.Create(messageArguments)));
    }

    internal Diagnostic ToDiagnostic(DiagnosticDescriptor descriptor)
    {
        Location location = string.IsNullOrEmpty(_filePath)
            ? Location.None
            : Location.Create(_filePath, _textSpan, _lineSpan);

        object[] arguments = new object[_messageArguments.Count];
        for (int index = 0; index < arguments.Length; index++)
        {
            arguments[index] = _messageArguments[index];
        }

        return Diagnostic.Create(descriptor, location, arguments);
    }

    public bool Equals(DiagnosticInfo other) =>
        _descriptorId == other._descriptorId
        && _filePath == other._filePath
        && _textSpan == other._textSpan
        && _lineSpan.Equals(other._lineSpan)
        && _messageArguments.Equals(other._messageArguments);

    public override bool Equals(object? obj) => obj is DiagnosticInfo other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        hash = (hash * 31) + (_descriptorId?.GetHashCode() ?? 0);
        hash = (hash * 31) + (_filePath?.GetHashCode() ?? 0);
        hash = (hash * 31) + _textSpan.GetHashCode();
        hash = (hash * 31) + _messageArguments.GetHashCode();
        return hash;
    }
}
