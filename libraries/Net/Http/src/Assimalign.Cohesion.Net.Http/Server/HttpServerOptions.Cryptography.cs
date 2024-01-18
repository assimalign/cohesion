
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Cryptography;

public partial class HttpServerOptions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="certificateManager"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void UseCertificateManager(ICertificateManager certificateManager)
    {
        if (certificateManager is null)
        {
            throw new ArgumentNullException(nameof(certificateManager));
        }

        this.certificateManager = certificateManager;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="certificateManager"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void UseCertificateManager(Func<ICertificateManager> certificateManager)
    {
        if (certificateManager is null)
        {
            throw new ArgumentNullException(nameof(certificateManager));
        }

        UseCertificateManager(certificateManager.Invoke());
    }
}
