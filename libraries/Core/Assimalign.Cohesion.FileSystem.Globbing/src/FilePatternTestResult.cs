namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
public struct FilePatternTestResult
{
    public static readonly FilePatternTestResult Failed = new FilePatternTestResult(isSuccessful: false, stem: null);

    public bool IsSuccessful { get; }
    public string Stem { get; }

    private FilePatternTestResult(bool isSuccessful, string stem)
    {
        IsSuccessful = isSuccessful;
        Stem = stem;
    }

    public static FilePatternTestResult Success(string stem)
    {
        return new FilePatternTestResult(isSuccessful: true, stem: stem);
    }
}
