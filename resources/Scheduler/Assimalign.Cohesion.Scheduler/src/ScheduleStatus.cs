using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Scheduler;

public enum ScheduleStatus
{
    Disabled,
    Stopped,
    Running,
    Idle,
    Paused,
    Failed,
    Cancelled,

    //Queued,
    //Done,
    //DueDone,

    //Batched
}
