namespace Assimalign.Cohesion.Scheduler.Cron;

/// <summary>
/// Identifies one field in a five-field cron expression.
/// </summary>
public enum CrontabFieldKind
{
    /// <summary>The minute field.</summary>
    Minute,

    /// <summary>The hour field.</summary>
    Hour,

    /// <summary>The day-of-month field.</summary>
    DayOfMonth,

    /// <summary>The month field.</summary>
    Month,

    /// <summary>The day-of-week field.</summary>
    DayOfWeek,
}
