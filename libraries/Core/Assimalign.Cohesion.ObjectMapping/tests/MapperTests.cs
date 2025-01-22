using Assimalign.Cohesion.ObjectMapping;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public partial class MapperTests
{
    public class Person1
    {
        public int? Age { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
        public DateTime Birthdate { get; set; }
        public IDictionary<string, Person1> Following { get; set; } // id:  person
        public Person1Address PrimaryAddress { get; set; }

        public IEnumerable<Person1Address> OtherAddresses { get; set; }
    }

    public class Person1Address
    {
        public string StreetOne { get; set; }
        public string City { get; set; }
    }




    public class Person2
    {
        public Person2Details Details { get; set; }
    }
    public class Person2Details
    {
        public int Age { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
        public DateTime? Birthdate { get; set; }
        public Person2Address PrimaryAddress { get; set; }
        public IEnumerable<Person2Following> Following { get; set; }

        public IEnumerable<Person2Address> OtherAddresses { get; set; }
    }

    public class Person2Address
    {
        public string StreetOne { get; set; }
        public string City { get; set; }
    }
    public class Person2Following
    {
        public string Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleName { get; set; }
    }




    public partial class MapperProfileTest : MapperProfile<Person1, Person2Details>
    {
        public override void Configure(IMapperActionDescriptor<Person1, Person2Details> descriptor)
        {
            descriptor
                .MapMember(target => target.FirstName, source => source.FirstName);
        }
    }



    [Fact]
    public void MappingTest()
    {
        

       

    }

    //public class Factory : MapperFactory
    //{
    //    public override IMapperFactory Configure(IMapperFactoryBuilder builder)
    //    {

    //        builder.Configure


    //        return this;
    //    }
    //}

}