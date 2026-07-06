using System;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration;

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
    private readonly string _messageArgument;

    private DiagnosticInfo(string descriptorId, string filePath, TextSpan textSpan, LinePositionSpan lineSpan, string messageArgument)
    {
        _descriptorId = descriptorId;
        _filePath = filePath;
        _textSpan = textSpan;
        _lineSpan = lineSpan;
        _messageArgument = messageArgument;
    }

    internal static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, string messageArgument)
    {
        var span = location?.GetLineSpan() ?? default;
        return new DiagnosticInfo(
            descriptor.Id,
            location?.SourceTree?.FilePath ?? string.Empty,
            location?.SourceSpan ?? default,
            span.Span,
            messageArgument);
    }

    internal Diagnostic ToDiagnostic(DiagnosticDescriptor descriptor)
    {
        var location = string.IsNullOrEmpty(_filePath)
            ? Location.None
            : Location.Create(_filePath, _textSpan, _lineSpan);
        return Diagnostic.Create(descriptor, location, _messageArgument);
    }

    internal string DescriptorId => _descriptorId;

    public bool Equals(DiagnosticInfo other) =>
        _descriptorId == other._descriptorId
        && _filePath == other._filePath
        && _textSpan == other._textSpan
        && _lineSpan.Equals(other._lineSpan)
        && _messageArgument == other._messageArgument;

    public override bool Equals(object? obj) => obj is DiagnosticInfo other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        hash = (hash * 31) + _descriptorId.GetHashCode();
        hash = (hash * 31) + _filePath.GetHashCode();
        hash = (hash * 31) + _textSpan.GetHashCode();
        hash = (hash * 31) + _messageArgument.GetHashCode();
        return hash;
    }
}
