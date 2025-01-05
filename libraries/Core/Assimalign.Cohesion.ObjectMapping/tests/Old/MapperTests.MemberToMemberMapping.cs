using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Extensions.Mapping.Tests;

using Assimalign.Extensions.Mapping;
using System.Diagnostics;

public partial class MapperTests
{

    public partial class MapperProfileBuilderTest : MapperProfileBuilder
    {
        protected override void OnBuild(IMapperProfileBuilder builder)
        {
            builder
                .CreateProfile<Person1, Person2>(descriptor =>
                {
                    descriptor
                        .MapMember(target => target.Age, source => source.Details.Age)
                        .MapMember(target => target.Birthdate, source => source.Details.Birthdate.GetValueOrDefault())
                        .MapMember(target => target.FirstName, source => source.Details.FirstName)
                        .MapMember(target => target.LastName, source => source.Details.LastName)
                        .MapMember("MiddleName", "Details.MiddleName")
                        .MapMemberEnumerables(target => target.OtherAddresses, source => source.Details.OtherAddresses);
                })
                .CreateProfile<Person2, Person1>(descriptor =>
                {
                    descriptor
                        // Points Nested type to a corresponding type to use
                        .MapMemberTypes(target => target.Details, source => source);
                })
                .CreateProfile<Person2Details, Person1>(descriptor =>
                {
                    descriptor
                        .MapMember(target => target.Age, source => source.Age.GetValueOrDefault())
                        .MapMember(target => target.Birthdate, source => new Nullable<DateTime>(source.Birthdate))
                        .MapMember(target => target.FirstName, source => source.FirstName)
                        .MapMember(target => target.LastName, source => source.LastName)
                        .MapMember("MiddleName", "MiddleName")
                        .MapMemberEnumerables(target => target.OtherAddresses, source => source.OtherAddresses);

                })
                .CreateProfile<Person1Address, Person2Address>(descriptor =>
                {
                    descriptor.MapAllProperties();
                })
                .CreateProfile<Person2Address, Person1Address>(descriptor =>
                {
                    descriptor.MapAllProperties();
                });
        }
    }

    public partial class MapperMemberToMemberProfile : MapperProfile<Person1, Person2>
    {
        public override void Configure(IMapperActionDescriptor<Person1, Person2> descriptor)
        {
            descriptor
                .MapMember(target => target.Age, source => source.Details.Age)
                .MapMember(target => target.Birthdate, source => source.Details.Birthdate.GetValueOrDefault())
                .MapMember(target => target.FirstName, source => source.Details.FirstName)
                .MapMember(target => target.LastName, source => source.Details.LastName)
                .MapMember("MiddleName", "Details.MiddleName")
                .MapMember(target => target.Following, source => source.Details.Following.ToDictionary(key => key.Id, value => new Person1()
                {
                    FirstName = value.FirstName
                }))
                .MapMemberEnumerables(target => target.OtherAddresses, source => source.Details.OtherAddresses);
        }
    }


    //public partial class MapperMemberToMember1Profile : MapperProfile<Person2Details, Person1>
    //{
    //    public override void Configure(IMapperActionDescriptor<Person2Details, Person1> descriptor)
    //    {
    //        descriptor
    //            .MapProfile(target => target.Following, source => source.Following, descripto =>
    //            {
    //                descripto
    //                    .MapMember(target => target.FirstName, source => source.Value.FirstName)
    //                    .MapAllFields();
    //            });
    //    }
    //}

    [Fact]
    public void RunMemberToMemberTest()
    {
        var stopwatch = new Stopwatch();
        var factory = MapperFactory.Configure(factory =>
        {
            factory.AddMapper("default", new MapperProfileBuilderTest(), options =>
            {
                options.CollectionHandling = MapperCollectionHandling.Merge;
            });
        });

        var mapper = factory.CreateMapper("default");

        //var mapper = Mapper.Create(configure =>
        //{
        //    configure.CollectionHandling = MapperCollectionHandling.Merge;
        //    configure.AddProfile(new MapperMemberToMemberProfile());
        //});

        var person1 = new Person1()
        {
            Following = new Dictionary<string, Person1>()
            {
                {
                    "ccrawford",
                    new Person1()
                    {
                        FirstName = "Chase"
                    }
                }
            },
            OtherAddresses = new Person1Address[]
            {
                new Person1Address()
                {
                    City = "Charlotte",
                    StreetOne = "1010 Somewhere St"
                }
            }
        };

        var person2 = new Person2()
        {
            Details = new Person2Details()
            {
                Age = 12,
                FirstName = "Chase",
                LastName = "Crawford",
                MiddleName = "Ryan",
                Following = new[]
                {
                    new Person2Following()
                    {
                        Id = "cbowers",
                        FirstName = "Charles",
                        LastName = "Bowers"
                    }
                },
                PrimaryAddress = new Person2Address()
                {
                    StreetOne = "1010 Kenilworth Ave"
                },
                OtherAddresses = new Person2Address[]
                {
                    new Person2Address()
                    {
                        City = "Charlotte 2",
                        StreetOne = "1010 Somewhere St"
                    }
                }
            }
        };

        stopwatch.Start();
        var person = mapper.Map(person1, person2);
        stopwatch.Stop();

        var pswitch = mapper.Map<Person2>(person1);

        Assert.Equal("Chase", person.FirstName);
        Assert.Equal("Crawford", person.LastName);
        Assert.Equal("Ryan", person.MiddleName);

    }
}
