using System.Net.Http;

namespace Assimalign.Cohesion.Net.Http;


public interface IHttpClientFactory 
{

    HttpClient CreateClient(string name);
}