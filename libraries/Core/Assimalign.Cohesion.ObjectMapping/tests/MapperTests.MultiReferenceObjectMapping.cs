using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Extensions.Mapping.Tests;

using Assimalign.Extensions.Mapping;


public partial class MapperTests
{
    public class TestPersonTarget
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int Age { get; set; }
    }

    public class TestUserSource1
    {
        public string FirstName { get; set; }
    }

    public class TestUserSource2
    {
        public string LastName { get; set; }
        public int? Age { get; set; }
    }

    public class PersonProfile1 : MapperProfile<TestPersonTarget, TestUserSource1>
    {
        public override void Configure(IMapperActionDescriptor<TestPersonTarget, TestUserSource1> descriptor)
        {
            descriptor
                .MapMember(target => target.FirstName, source => source.FirstName);
        }
    }
    public class PersonProfile2 : MapperProfile<TestPersonTarget, TestUserSource2>
    {
        public override void Configure(IMapperActionDescriptor<TestPersonTarget, TestUserSource2> descriptor)
        {
            descriptor
                .MapMember(target => target.LastName, source => source.LastName)
                .MapMember(target => target.Age, source => source.Age.GetValueOrDefault());
        }
    }

    [Fact] // Pass in multiple reference source types to map to one target type
    public void TestMulitObjectMap()
    {
        var mapper = Mapper.Create(builder =>
        {
            builder.AddProfile(new PersonProfile1());
            builder.AddProfile(new PersonProfile2());

        }); 

        var s1 = new TestUserSource1() { FirstName = "Chase" };
        var s2 = new TestUserSource2() { LastName = "Crawford", Age = 25 };

        var results = mapper.Map<TestPersonTarget>(s1, s2);

        Assert.Equal("Chase", results.FirstName);
        Assert.Equal("Crawford", results.LastName);
        Assert.Equal(25, results.Age);
    }
}
