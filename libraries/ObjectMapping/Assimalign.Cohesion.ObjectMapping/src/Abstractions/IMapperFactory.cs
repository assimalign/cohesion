namespace Assimalign.Cohesion.ObjectMapping;

///<summary>
///
///</summary>
public interface IMapperFactory
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="mapperName"></param>
    /// <returns></returns>
    IMapper Create(string mapperName);
}