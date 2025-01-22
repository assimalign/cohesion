namespace Assimalign.Cohesion.Net.Http;


public enum HttpExceptionCode
{
    Unknown = 0,
    /// <summary>
    /// Occurs when the incoming request could not be read.
    /// </summary>
    ReadingError,
    /// <summary>
    /// Occurs when the outgoing response could not be written.
    /// </summary>
    WritingError,
    /// <summary>
    /// 
    /// </summary>
    ExecutionError,
}
