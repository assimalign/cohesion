﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Assimalign.Cohesion.DependencyInjection.Specification.Fakes
{
    public class ClassWithAbstractClassConstraint<T> : IFakeOpenGenericService<T>
        where T : AbstractClass
    {
        public ClassWithAbstractClassConstraint(T value) => Value = value;

        public T Value { get; }
    }
}
