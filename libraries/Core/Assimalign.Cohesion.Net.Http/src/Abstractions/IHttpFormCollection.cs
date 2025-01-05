using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

public interface IHttpFormCollection
{
    IHttpFormFileCollection Files { get; }
}
public interface IHttpFormFileCollection
{

}
public interface IHttpFormFile
{

}