using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace Assimalign.Cohesion;




/// <summary> 
///
/// </summary>
/// <remarks>
/// Crontab expression format: <para />
/// <para /> 
/// *****	<para />
/// -----	 <para />
/// | | | | |	<para />
/// | | | | +-------- day of week (0 - 6) (Sunday=0)<para />
/// | | | +---------- month (1 - 12)<para />
/// | | +------------ day of month (1 - 31) <para />
/// | +-------------- hour (0 - 23) <para />
/// +---------------- min (0 - 59) <para />
/// 
/// Star (*) in the value field above means all legal values as in
/// braces for that column. The value column can have a * or a list
/// of elements separated by commas. An element is either a number in
/// the ranges shown above or two numbers in the range separated by a
/// hyphen (meaning an inclusive range).
///
/// Source: http://www.adminschoice.com/docs/crontab.htm
///
///
/// Six-part expression format:
///
/// * * * * * *
/// - - - - - -
/// ||||||
/// | | | | | +--- day of week (0 - 6) (Sunday=0)
/// | | | | +----- month (1 - 12)
/// | | | +------- day of month (1 - 31)
/// | | +--------- hour (0 - 23)
/// | +----------- min (0 - 59)
/// +------------- sec (0 - 59)
/// 
/// The six-part expression behaves similarly to the traditional
/// crontab format except that it can denotate more precise schedules
/// that use a seconds component.
/// </remarks>
[Serializable]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Crontab : IEquatable<Crontab>, IEnumerable<DateTime>, ISerializable, IFormattable
{
    public const char RangValue = '-';
    public const char StepValue = '/';
    public const char Any = '*';
    public const char ListSeparator = ',';
    /** <summary> */
    /** This is a test */
    /** another test */
    /** </summary> */
    private Crontab(CrontabField minute, CrontabField hour, CrontabField dayOfMonth, CrontabField month, CrontabField dayOfWeek)
    {
        this.Minute = minute;
        this.Hour = hour;
        this.DayOfMonth = dayOfMonth;
        this.Month = month;
        this.DayOfWeek = dayOfWeek;
    }

    /// <summary>
    /// 
    /// </summary>
    public CrontabField Minute { get; }
    /// <summary>
    /// 
    /// </summary>
    public CrontabField Hour { get; }
    /// <summary>
    /// 
    /// </summary>
    public CrontabField DayOfMonth { get; }
    /// <summary>
    /// 
    /// </summary>
    public CrontabField Month { get; }
    /// <summary>
    /// 
    /// </summary>
    public CrontabField DayOfWeek { get; }

    /// <summary>
    /// Get's the amount of time until the next occurrence from the current local time.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public TimeSpan GetTimeSpan()
    {
        return GetDateTime().Subtract(DateTime.Now);
    }

    /// <summary>
    /// Get's the next occurrence in DateTime from the current local time.
    /// </summary>
    /// <returns></returns>
    public DateTime GetDateTime()
    {
        return GetDateTime(DateTime.Now);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="start"></param>
    /// <returns></returns>
    public DateTime GetDateTime(DateTime start)
    {
        var index = 0;
        var next = new DateTime(start.Year, 1, 1, 0, 0, 0);

    restart:
        for (int a = 0; a < 12; a++)
        {
            if (!Month.Occurrences.Contains(a + 1))
            {
                next = next.AddMonths(1);
                continue;
            }
            for (int b = 0; b < DateTime.DaysInMonth(next.Year, a + 1); b++)
            {
                if (!DayOfMonth.Occurrences.Contains(b + 1) || !DayOfWeek.Occurrences.Contains((int)next.DayOfWeek))
                {
                    next = next.AddDays(1);
                    continue;
                }
                for (int c = 0; c < 24; c++)
                {
                    if (!Hour.Occurrences.Contains(c))
                    {
                        next = next.AddHours(1);
                        continue;
                    }
                    for (int d = 0; d < 60; d++)
                    {
                        if (Minute.Occurrences.Contains(d) && next > start)
                        {
                            return next;
                        }
                        else
                        {
                            next = next.AddMinutes(1);
                        }
                    }
                }
            }
        }

        if (index >= 2)
        {
            throw new Exception("The provided expression was parsed but has an invalid tab. ");
        }
        index++;
        goto restart;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public bool Equals(Crontab other)
    {
        return
            this.Minute.Expression == other.Minute.Expression &&
            this.Hour.Expression == other.Hour.Expression &&
            this.DayOfMonth.Expression == other.DayOfMonth.Expression &&
            this.Month.Expression == other.Month.Expression &&
            this.DayOfWeek.Expression == other.DayOfWeek.Expression;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="format"></param>
    /// <param name="formatProvider"></param>
    /// <returns></returns>
    public string ToString(string format, IFormatProvider formatProvider)
    {
        switch (format)
        {
            case "E":
                {
                    return $"{Minute} {Hour} {DayOfMonth} {Month} {DayOfWeek}";
                }
            default:
                {
                    return $"{Minute} {Hour} {DayOfMonth} {Month} {DayOfWeek}";
                }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return ToString("E", default);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <returns></returns>
    public override bool Equals([NotNullWhen(true)] object instance) => instance is Crontab crontab ? Equals(crontab) : false;
    public static bool operator ==(Crontab left, Crontab right) => left.Equals(right);
    public static bool operator !=(Crontab left, Crontab right) => !left.Equals(right);

    public static implicit operator Crontab(string expression) => Crontab.Parse(expression);
    public static Crontab Parse(string expression)
    {
        var segments = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Let's ensure that the expression segments has the proper length 
        if (segments.Length != 5)
        {
            throw new ArgumentException("The expression is not in the proper format.");
        }

        var minute = default(CrontabField);      // index - 1 or 2
        var hour = default(CrontabField);
        var dayOfMonth = default(CrontabField);
        var month = default(CrontabField);
        var dayOfWeek = default(CrontabField);

        for (int i = 0; i < segments.Length; i++)
        {
            if (i == 0)
            {
                minute = CrontabField.ParseMinute(segments[i]);
                continue;
            }
            if (i == 1)
            {
                hour = CrontabField.ParseHour(segments[i]);
                continue;
            }
            if (i == 2)
            {
                dayOfMonth = CrontabField.ParseDayOfMonth(segments[i]);
                continue;
            }
            if (i == 3)
            {
                month = CrontabField.ParseMonth(segments[i]);
                continue;
            }
            if (i == 4)
            {
                dayOfWeek = CrontabField.ParseDayOfWeek(segments[i]);
                continue;
            }
        }

        return new Crontab(minute, hour, dayOfMonth, month, dayOfWeek);
    }


    void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
    {
        if (info is null)
        {
            throw new ArgumentNullException("info");
        }

        info.AddValue("crontabExpression", this.ToString());
    }

    #region Crontab Enumeration
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    public IEnumerator<DateTime> GetEnumerator()
    {
        return new CrontabEnumerator(this);
    }



    private class CrontabEnumerator : IEnumerator<DateTime>
    {
        private readonly Crontab crontab;
        private DateTime? index;

        public CrontabEnumerator(Crontab crontab)
        {
            this.crontab = crontab;
        }

        public DateTime Current
        {
            get
            {
                if (!index.HasValue)
                {
                    index = crontab.GetDateTime();
                    return index.GetValueOrDefault();
                }
                else
                {
                    index = crontab.GetDateTime(index.Value);
                    return index.GetValueOrDefault();
                }
            }
        }

        object IEnumerator.Current => this.Current;

        public void Dispose()
        {

        }

        public bool MoveNext() => true;

        public void Reset()
        {
            index = null;
        }
    }
    #endregion
}