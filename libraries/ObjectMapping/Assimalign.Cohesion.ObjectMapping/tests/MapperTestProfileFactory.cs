using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

using Assimalign.Cohesion.ObjectMapping;

public class MapperTestProfileFactory
{
    public partial class MapperProfileBuilderTest : MapperProfileBuilder
    {
        protected override void OnBuild(IMapperProfileBuilder builder)
        {
            //builder
            //    .CreateProfile<TestPerson, TestUser>(descriptor =>
            //    {
            //        descriptor
            //            .MapMember(target => target.Details.Age, source => source.Age)
            //            .MapMember(target => target.Details.Birthdate, source => source.Birthdate.GetValueOrDefault())
            //            .MapMember(target => target.Details.FirstName, source => source.FirstName)
            //            .MapMember(target => target.Details.LastName, source => source.LastName)
            //            .MapMember("Details.MiddleName", "MiddleName")
            //            .MapMemberEnumerables(target => target.OtherAddresses, source => source.Details.OtherAddresses);
            //    })
            //    .CreateProfile<Person2, Person1>(descriptor =>
            //    {
            //        descriptor
            //            // Points Nested type to a corresponding type to use
            //            .MapMemberTypes(target => target.Details, source => source);
            //    })
            //    .CreateProfile<Person2Details, Person1>(descriptor =>
            //    {
            //        descriptor
            //            .MapMember(target => target.Age, source => source.Age.GetValueOrDefault())
            //            .MapMember(target => target.Birthdate, source => new Nullable<DateTime>(source.Birthdate))
            //            .MapMember(target => target.FirstName, source => source.FirstName)
            //            .MapMember(target => target.LastName, source => source.LastName)
            //            .MapMember("MiddleName", "MiddleName")
            //            .MapMemberEnumerables(target => target.OtherAddresses, source => source.OtherAddresses);

            //    })
            //    .CreateProfile<Person1Address, Person2Address>(descriptor =>
            //    {
            //        descriptor.MapAllProperties();
            //    })
            //    .CreateProfile<Person2Address, Person1Address>(descriptor =>
            //    {
            //        descriptor.MapAllProperties();
            //    });
        }
    }

    [Fact]
    public void TestProfileCountForFactoryBuilder()
    {

    }
}
