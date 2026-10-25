namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Reports domain validation before changing business state.</summary>
/// <param name="IsValid">Whether the key and value may be applied.</param>
/// <param name="ErrorCode">A stable failure code when invalid.</param>
public sealed record ResourceStateValidation(bool IsValid, string? ErrorCode = null);
