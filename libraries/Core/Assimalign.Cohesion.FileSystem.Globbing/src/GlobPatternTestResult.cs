namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
public struct GlobPatternTestResult
{
    public static readonly GlobPatternTestResult Failed = new GlobPatternTestResult(isSuccessful: false, stem: null);

    public bool IsSuccessful { get; }
    public string Stem { get; }

    private GlobPatternTestResult(bool isSuccessful, string stem)
    {
        IsSuccessful = isSuccessful;
        Stem = stem;
    }

    public static GlobPatternTestResult Success(string stem)
    {
        return new GlobPatternTestResult(isSuccessful: true, stem: stem);
    }
}
