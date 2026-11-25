namespace Trmnl.Server
{
    using System.Runtime.CompilerServices;
    public class SimpleAsyncLazy<T> : Lazy<Task<T>>
    {
        public SimpleAsyncLazy(Func<Task<T>> taskFactory)
            : base(() => Task.Run(taskFactory)) { }

        public TaskAwaiter<T> GetAwaiter() => Value.GetAwaiter();
    }
}