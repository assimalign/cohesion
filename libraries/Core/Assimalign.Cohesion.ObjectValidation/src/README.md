# Assimalign Extensions Validation

- [Assimalign ComponentModel Fluent Validation](#assimalign-componentmodel-fluent-validation)
- [Concepts](#concepts)
- [Getting Started](#getting-started)
    - [1. Creating a Validation Profile](#1-creating-a-validation-profile)
    - [2. Building a Validator](#2-building-a-validator)
    - [3. Running Validation](#3-running-validation)


<br/>
<br/>


---
# Concepts
To create a cleaner implementation to fluent validation and allow for better extensibility.
 
The key pattern of encapsulation follows this flow: 
```
           Validator
(encapsulates) └─> Validation Profiles 
                            └─> Validation Items
                                        └─> Validation Rules
```

- **Validator:** The instance running the validation.
- **Validation Profile:** The configurable object which describes the validation items and rules.
- **Validation Item:** The item being described for validation. (This is usually the member or field of a Type)
- **Validation Rule:** The rules to be applied for a specific validation item.

<br/>
<br/>

---

# Getting Started

### 1. Creating a Validation Profile
First create a type and define the validation rules with the built ValidationProfile.

```csharp 

public class Person
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public DateTime Birthday { get; set; }
}


public class PersonValidationProfile : ValidationProfile<Person>
{

    public PersonValidationProfile()
    {
        // Specify that the validation stop of first failure
        base.ValidationMode = ValidationMode.Stop;
    }


    public override void Configure(IValidationRuleDescriptor<Person> descriptor)
    {
        descriptor.RuleFor(p => p.FirstName)
            .NotEmpty()
            .MaxLength(255);

        descriptor.RuleFor(p => p.LastName)
            .NotEmpty()
            .MaxLength(255);

        descriptor.RuleFor(p => p.Birthday)
            .LessThan(DateTime.Now)
            .NotEqual(default(DateTime));
    }
}
```

### 2. Building a Validator
Build a validator by passing the the 

```csharp 

var validator = Validator.Create(options => 
{
    // Optional configurations
    options.ThrowExceptionOnFailure = true; 

    // Required Configurations
    options.AddProfile(new PersonValidationProfile());
});

```


### 3. Running Validation
Instantiate the type and pass it through the validator

```csharp

var person = new Person() 
{
    FirstName = "Chase",
    LastName = "Crawford",
    Birthday = default(DateTime)
};

var validationResults = validator.Validate(person);


```