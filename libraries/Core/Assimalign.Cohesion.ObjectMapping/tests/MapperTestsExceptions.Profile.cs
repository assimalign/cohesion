using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Extensions.Mapping.Tests;

using Assimalign.Extensions.Mapping;
using Assimalign.Extensions.Mapping.Internal;

public partial class MapperTestsExceptions
{


    public partial class MapperProfileTestCase1 : MapperProfile<TestPerson, TestUser>
    {
        public override void Configure(IMapperActionDescriptor<TestPerson, TestUser> descriptor)
        {
            descriptor.MapMember(target => target.Details.FirstName, source => source.FirstName);
        }
    }

    [Fact] // When specifying target expression the member must be of the declaring type
    public void ExceptionIsThrownOnTargetChainedMemberWithProfileInheritenceTest()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var descriptor = new MapperActionDescriptor<TestPerson, TestUser>();
            var profile = new MapperProfileTestCase1();

            profile.Configure(descriptor);
        });
    }


    public partial class MapperProfileTestCase2 : MapperProfile<TestUser, TestPerson>
    {
        public override void Configure(IMapperActionDescriptor<TestUser, TestPerson> descriptor)
        {
            descriptor.MapMember(target => target.FirstName, source => source.Details.Age);
        }
    }

    [Fact] // When specifying target expression the source member must be assignable to target member
    public void ExceptionIsThrownOnInvalidAssigningMemberWithProfileInheritenceTest()
    {
        Assert.Throws<InvalidCastException>(() =>
        {
            var descriptor = new MapperActionDescriptor<TestUser, TestPerson>();
            var profile = new MapperProfileTestCase2();

            profile.Configure(descriptor);
        });
    }


    public partial class MapperProfileTestCase3 : MapperProfile<TestUser, TestPerson>
    {
        public override void Configure(IMapperActionDescriptor<TestUser, TestPerson> descriptor)
        {
            descriptor.MapMember(target => target.FirstName.ToLower(), source => source.Details.Age);
        }
    }

    [Fact] // When specifying target expression the target expression must be a member expression
    public void ExceptionIsThrownOnInvalidInvalidExpressionWithProfileInheritenceTest()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var descriptor = new MapperActionDescriptor<TestUser, TestPerson>();
            var profile = new MapperProfileTestCase3();

            profile.Configure(descriptor);
        });
    }
}
