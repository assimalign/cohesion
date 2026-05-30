namespace Assimalign.Cohesion.ObjectMapping;

public interface IMapperBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="profile"></param>
    /// <returns></returns>
    IMapperBuilder AddProfile(IMapperProfile profile);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IMapper Build();
}
