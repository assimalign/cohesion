﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectMapping;

public interface IMapperStrategy
{
    IMapper Create(IEnumerable<IMapperProfile> profiles);
}
