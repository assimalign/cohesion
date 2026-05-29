using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationPipeline
{

    Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default);

}