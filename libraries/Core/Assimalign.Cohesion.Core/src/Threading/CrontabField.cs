using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion;

/// <summary>
/// 
/// </summary>
public readonly struct CrontabField
{
	private CrontabField(CrontabFieldKind kind, string expression, int minBoundary, int maxBoundary, int[] occurances)
	{
		this.Kind = kind;
		this.Expression = expression;
		this.MinBoundary = minBoundary;
		this.MaxBoundary = maxBoundary;
		this.Occurrences = occurances;
	}

	/// <summary>
	/// 
	/// </summary>
	public bool IsAll => this.Expression == "*";
	/// <summary>
	/// An array of all the possible occurrences 
	/// </summary>
	public int[] Occurrences { get; }
	/// <summary>
	/// 
	/// </summary>
	public CrontabFieldKind Kind { get; }
	/// <summary>
	/// 
	/// </summary>
	public string Expression { get; }
	/// <summary>
	/// 
	/// </summary>
	public int MaxBoundary { get; }
	/// <summary>
	/// 
	/// </summary>
	public int MinBoundary { get; }

	public static CrontabField ParseMinute(string expression)
	{
		return new CrontabField(
			CrontabFieldKind.Minute,
			expression,
			0,
			59,
			GetOccurrences(expression, 0, 59));
	}
	public static CrontabField ParseHour(string expression)
	{
		return new CrontabField(
			CrontabFieldKind.Minute,
			expression,
			0,
			23,
			GetOccurrences(expression, 0, 23));
	}
	public static CrontabField ParseDayOfMonth(string expression)
	{
		return new CrontabField(
			CrontabFieldKind.Minute,
			expression,
			1,
			31,
			GetOccurrences(expression, 1, 31)); ;
	}
	public static CrontabField ParseMonth(string expression)
	{
		return new CrontabField(
			CrontabFieldKind.Minute,
			expression,
			1,
			12,
			GetOccurrences(expression, 1, 12));
	}
	public static CrontabField ParseDayOfWeek(string expression)
	{
		return new CrontabField(
			CrontabFieldKind.Minute,
			expression,
			0,
			6,
			GetOccurrences(expression, 0, 6));
	}
	private static int[] GetOccurrences(string expression, int min, int max)
	{
		if (expression == "*")
		{
			var seed = min;
			var occurrences = new int[max - min + 1];
			for (int i = 0; i < occurrences.Length; i++)
			{
				occurrences[i] = seed;
				seed++;
			}
			return occurrences;
		}
		if (expression.Contains('/'))
		{
			var steps = expression.Split('/');
			var boundariesStep = steps[0];
			var intervalsStep = steps[1];

			// Check for invalid step format
			if (steps.Length != 2)
			{
				throw new FormatException($"The following expression '{expression}' has either more than one step delimiter -> '/', or is invalid.");
			}
			if (boundariesStep.Equals("*"))
			{
				var occurrences = new List<int>();
				// Indicates a list of varied intervals between 0 and 59
				// Example: */2,5,7
				//      Occurrence A: 2, 4, 6, 8,...
				//      Occurrence B: 5, 10, 15,....
				//      Occurrence C: 7, 14, 21, 28,...
				// NOTE: Once the occurrence list has been built, select only distinct int.
				//       The varied interval can sometimes have duplicate values
				if (intervalsStep.Contains(','))
				{
					var intervals = intervalsStep.Split(',');

					for (int i = 0; i < intervals.Length; i++)
					{
						occurrences.AddRange(GetOccurrences($"*/{intervals[i]}", min, max));
					}
				}
				else
				{
					var interval = int.Parse(intervalsStep);
					if (interval > max)
					{
						throw new ArgumentException($"The step value '{interval}' in expression '{expression}' cannot be greater than '{max}'.");
					}
					for (int i = min; i <= max; i = i + interval)
					{
						occurrences.Add(i);
					}
				}

				occurrences.Sort();
				return occurrences.Distinct().ToArray();
			}
			// Check for a list of boundaries
			if (boundariesStep.Contains(','))
			{
				var occurrences = new List<int>();
				var boundariesList = boundariesStep.Split(',');

				for (int i = 0; i < boundariesList.Length; i++)
				{
					var lower = min;
					var upper = max;

					// Is the current boundary a range or single value
					if (boundariesList[i].Contains('-'))
					{
						lower = int.Parse(boundariesList[i].Split('-')[0]);
						upper = int.Parse(boundariesList[i].Split('-')[1]);
					}
					else
					{
						lower = int.Parse(boundariesList[i]);
					}
					// Check if the parse boundaries are greater of less than the default boundaries
					if (lower < min || upper > max)
					{
						throw new ArgumentOutOfRangeException("");
					}
					// Now check if the intervals step is also a list
					if (intervalsStep.Contains(','))
					{
						var intervals = intervalsStep.Split(',');

						for (int c = 0; c < intervals.Length; c++)
						{
							occurrences.AddRange(GetOccurrences($"*/{intervals[c]}", lower, upper));
						}
					}
					else
					{
						occurrences.AddRange(GetOccurrences($"*/{intervalsStep}", lower, upper));
					}
				}

				occurrences.Sort();
				return occurrences.Distinct().ToArray();
			}
			// Check if boundaries is a range
			if (boundariesStep.Contains('-'))
			{
				var lower = int.Parse(boundariesStep.Split('-')[0]);
				var upper = int.Parse(boundariesStep.Split('-')[1]);
				var occurrences = new List<int>();

				// Check if the parse boundaries are greater of less than the default boundaries
				if (lower < min || upper > max)
				{
					throw new ArgumentOutOfRangeException("");
				}
				if (intervalsStep.Contains(','))
				{
					var intervals = intervalsStep.Split(',');
					for (int i = 0; i < intervals.Length; i++)
					{
						occurrences.AddRange(GetOccurrences($"{lower}-{upper}/{intervals[i]}", min, max));
					}
				}
				else
				{
					var interval = int.Parse(intervalsStep);

					for (int i = lower; i < upper; i = i + interval)
					{
						occurrences.Add(i);
					}
				}

				occurrences.Sort();
				return occurrences.Distinct().ToArray();
			}
		}
		if (expression.Contains(','))
		{
			var boundaies = expression.Split(',');
			var occurrences = new List<int>();

			for (int i = 0; i < boundaies.Length; i++)
			{
				if (boundaies[i].Contains('-'))
				{
					var lower = int.Parse(boundaies[i].Split('-')[0]);
					var upper = int.Parse(boundaies[i].Split('-')[1]);

					occurrences.AddRange(GetOccurrences($"{lower}-{upper}", min, max));
				}
				else
				{
					occurrences.AddRange(GetOccurrences(boundaies[i], min, max));
				}
			}

			occurrences.Sort();
			return occurrences.ToArray();
		}
		if (expression.Contains('-'))
		{
			var lower = int.Parse(expression.Split('-')[0]);
			var upper = int.Parse(expression.Split('-')[1]);
			var occurrences = new List<int>();

			if (lower >= upper)
			{
				throw new ArgumentException($"The provided range {lower}-{upper} within the given expression is invalid.");
			}
			for (int i = lower; i < upper + 1; i++)
			{
				occurrences.Add(i);
			}

			occurrences.Sort();
			return occurrences.ToArray();
		}
		else
		{
			var value = int.Parse(expression);
			if (value > max || value < min)
			{
				throw new ArgumentOutOfRangeException("expression", value, $"The value(s) must be between {min} and {max}. The value {value} within the provided expression failed.");
			}
			return new int[1] { value };
		}
	}


    public override string ToString()
    {
		return this.Expression;
    }
}