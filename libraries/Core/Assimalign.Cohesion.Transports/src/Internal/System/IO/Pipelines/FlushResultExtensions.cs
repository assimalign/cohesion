using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace System.IO.Pipelines;

internal static class FlushResultExtensions
{

    extension(ValueTask<FlushResult> valueTask)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Task GetAsTask()
        {
            // Try to avoid the allocation from AsTask
            if (valueTask.IsCompletedSuccessfully)
            {
                // Signal consumption to the IValueTaskSource
                valueTask.GetAwaiter().GetResult();
                return Task.CompletedTask;
            }
            else
            {
                return valueTask.AsTask();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask GetAsValueTask()
        {
            // Try to avoid the allocation from AsTask
            if (valueTask.IsCompletedSuccessfully)
            {
                // Signal consumption to the IValueTaskSource
                valueTask.GetAwaiter().GetResult();
                return default;
            }
            else
            {
                return new ValueTask(valueTask.AsTask());
            }
        }
    }
}
