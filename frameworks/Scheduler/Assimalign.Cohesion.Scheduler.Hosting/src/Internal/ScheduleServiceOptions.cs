using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Scheduler.Internal;

internal class ScheduleServiceOptions
{

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    public List<ISchedule> Schedules { get; set; } = new List<ISchedule>();
}
