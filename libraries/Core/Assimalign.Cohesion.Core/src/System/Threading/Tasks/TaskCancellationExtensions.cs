using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Threading.Tasks;

public static class TaskCancellationExtensions
{

    //public static Task OnCancel<T>(this Task<T> task, Action onCancel)
    //{
    //    var taskCompletionSource = new TaskCompletionSource()
    //    return task.ContinueWith<T>((_,t) =>
    //    {

    //    });
    //}
    //    public static Task<Either<None, Cancelled>> CaptureCancellation(this Task task) =>
    //    CaptureCancellation(task.ToTaskOfT<None>());

    //    public static Task<Either<T, Cancelled>> CaptureCancellation<T>(this Task<T> task) =>
    //        CaptureCancellation(task,
    //            new TaskCompletionSource<Either<T, Cancelled>>(),
    //            tcs => tcs.TrySetResult(new Cancelled()),
    //            tcs => tcs.TrySetResult(task.Result));


    //    public static Task<T> CaptureCancellation<T>(this Task<T> task, onCancel)

    //    public static Task<T> CaptureCancellation<T>(Task<T> task,
    //        TaskCompletionSource<T> taskCompletionSource,
    //        Action<TaskCompletionSource<T>> onCancel,
    //        Action<TaskCompletionSource<T?>> onSuccess)
    //    {
    //        task.ContinueWith(_ =>
    //        {
    //            if (task.IsCanceled || task.IsFaulted && task.Exception.InnerException is OperationCanceledException)
    //            {
    //                onCancel.Invoke(taskCompletionSource);
    //            }
    //            else
    //            {
    //                onSuccess.Invoke(taskCompletionSource);
    //            }
    //        });
    //        return taskCompletionSource.Task;
    //    }

    //    private static Task<T> ToTaskOfT<T>(this Task task, T? value = default)
    //    {
    //        if (task is Task<T> t)
    //        {
    //            return t;
    //        }

    //        var taskCompletionSource = new TaskCompletionSource<T>();

    //        task.ContinueWith(ant =>
    //        {
    //            if (ant.IsCanceled) taskCompletionSource.SetCanceled();
    //            else if (ant.IsFaulted) taskCompletionSource.SetException(ant.Exception.InnerException);
    //            else taskCompletionSource.SetResult(value);
    //        });
    //        return taskCompletionSource.Task;
    //    }
}
