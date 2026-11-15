using System;

namespace Assimalign.Cohesion.Database.Text.Internal;

[Flags]
internal enum Porter2Steps
{
    None = 0,
    Exception1 = 1,
    ShortWord = 2,
    Prelude = 4,
    Regions = 8,
    Step0 = 16,
    Step1A = 32,
    Exception2 = 64,
    Step1B = 128,
    Step1C = 256,
    Step2 = 512,
    Step3 = 1024,
    Step4 = 2048,
    Step5 = 4096,
}
