using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

using Assimalign.Cohesion.ObjectMapping;

public partial class MapperTestsExceptions
{


    [Fact] // When specifying target expression the member must be of the declaring type
    public void ExceptionIsThrownOnTargetChainedMemberWithFluentFactoryTest()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var factory = MapperFactory.Configure(factory =>
            {
                factory.AddMapper("default", builder =>
                {
                    builder.CreateProfile<TestPerson, TestUser>(descriptor =>
                    {
                        descriptor.MapMember(target => target.Details.FirstName, source => source.FirstName);
                    });
                });
            });

            
        });
    }


    [Fact] // When specifying target expression the source member must be assignable to target member
    public void ExceptionIsThrownOnInvalidAssigningMemberWithFluentFactoryTest()
    {
        Assert.Throws<InvalidCastException>(() =>
        {
            var factory = MapperFactory.Configure(factory =>
            {
                factory.AddMapper("default", builder =>
                {
                    builder.CreateProfile<TestUser, TestPerson>(descriptor =>
                    {
                        descriptor.MapMember(target => target.FirstName, source => source.Details.Age);
                    });
                });
            });

            
        });
    }


    [Fact] // When specifying target expression the target expression must be a member expression
    public void ExceptionIsThrownOnInvalidInvalidExpressionWithFluentFactoryTest()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var factory = MapperFactory.Configure(factory =>
            {
                factory.AddMapper("default", builder =>
                {
                    builder.CreateProfile<TestUser, TestPerson>(descriptor =>
                    {
                        descriptor.MapMember(target => target.FirstName.ToLower(), source => source.Details.Age);
                    });
                });
            });             
        });
    }




    public partial class MapperProfileBuilderTestCase1 : MapperProfileBuilder
    {
        protected override void OnBuild(IMapperProfileBuilder builder)
        {
            builder.CreateProfile<TestPerson, TestUser>(descriptor =>
            {
                descriptor.MapMember(target => target.Details.FirstName, source => source.FirstName);
            });
        }
    }


    [Fact] // When specifying target expression the member must be of the declaring type
    public void ExceptionIsThrownOnTargetChainedMemberWithFactoryTest()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var factory = MapperFactory.Configure(factory =>
            {
                factory.AddMapper("default", new MapperProfileBuilderTestCase1());
            });           
        });
    }


    public partial class MapperProfileBuilderTestCase2 : MapperProfileBuilder
    {
        protected override void OnBuild(IMapperProfileBuilder builder)
        {
            builder.CreateProfile<TestUser, TestPerson>(descriptor =>
            {
                descriptor.MapMember(target => target.FirstName, source => source.Details.Age);
            });
        }
    }

    [Fact] // When specifying target expression the source member must be assignable to target member
    public void ExceptionIsThrownOnInvalidAssigningMemberWithFactoryTest()
    {
        Assert.Throws<InvalidCastException>(() =>
        {
            var factory = MapperFactory.Configure(factory =>
            {
                factory.AddMapper("default", new MapperProfileBuilderTestCase2());
            });
        });
    }

    public partial class MapperProfileBuilderTestCase3 : MapperProfileBuilder
    {
        protected override void OnBuild(IMapperProfileBuilder builder)
        {
            builder.CreateProfile<TestUser, TestPerson>(descriptor =>
            {
                descriptor.MapMember(target => target.FirstName.ToLower(), source => source.Details.Age);
            });
        }
    }


    [Fact] // When specifying target expression the target expression must be a member expression
    public void ExceptionIsThrownOnInvalidInvalidExpressionWithFactoryTest()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var factory = MapperFactory.Configure(factory =>
            {
                factory.AddMapper("default", new MapperProfileBuilderTestCase3());
            });
        });
    }
}
