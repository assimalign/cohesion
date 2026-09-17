namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal readonly record struct ProbeAttemptResult(bool Succeeded, bool FailFast, string Detail)
{
    public static ProbeAttemptResult Success(string detail) => new(true, false, detail);

    public static ProbeAttemptResult Failure(string detail, bool failFast = false)
        => new(false, failFast, detail);
}
