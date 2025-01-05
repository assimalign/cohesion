using System;

namespace Assimalign.Cohesion.Net.Cryptography;

using Assimalign.Cohesion.Net.Cryptography.Internal;

public abstract class CertificateManagerException : Exception
{
	public CertificateManagerException(string message) : base(message) { }
	public CertificateManagerException(string message, Exception innerException): base(message, innerException) { }


	internal static CertificateManagerException CertificateNotFound(string thumbprint)
	{
		return new CertificateNotFoundException(thumbprint);
	}
}
