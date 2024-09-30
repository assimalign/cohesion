using System;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public abstract class HostException : Exception
{
	public HostException(string message): base(message) { }
	public HostException(string message, Exception innerException)  : base(message, innerException) { }
}
