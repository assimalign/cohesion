using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Cryptography.Internal;

internal sealed class CertificateNotFoundException : CertificateManagerException
{
	public const string message = "No certificate was found for the given ThumbPrint: '{0}'";
	public CertificateNotFoundException(string thumbprint): base (string.Format(message, thumbprint)) { }
}
