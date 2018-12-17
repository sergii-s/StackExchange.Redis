using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StackExchange.Redis
{
    abstract partial class ResultBox
    {
        protected Exception exception;

        public void SetException(Exception exception)
        {
            this.exception = exception;
            //try
            //{
            //    throw exception;
            //}
            //catch (Exception caught)
            //{ // stacktrace etc
            //    this.exception = caught;
            //}
        }

        public abstract bool TryComplete(bool isAsync);

        [Conditional("DEBUG")]
        protected static void IncrementAllocationCount()
        {
            OnAllocated();
        }

        static partial void OnAllocated();
    }

    sealed class MultyResultBox<T> : ResultBox
    {
        private static readonly MultyResultBox<T>[] store = new MultyResultBox<T>[64];

        private object stateOrCompletionSource;

        private ConcurrentDictionary<Message, T> values;
        private int countNeeded;
        private int countDone;
        
        public MultyResultBox(object stateOrCompletionSource, int count)
        {
            this.stateOrCompletionSource = stateOrCompletionSource;
            this.countNeeded = count;
            this.countDone = 0;
            this.values = new ConcurrentDictionary<Message, T>();
        }

//        public object AsyncState =>
//            stateOrCompletionSource is TaskCompletionSource<T>
//                ? ((TaskCompletionSource<T>) stateOrCompletionSource).Task.AsyncState
//                : stateOrCompletionSource;

        public static MultyResultBox<T> Get(object stateOrCompletionSource, int count)
        {
            MultyResultBox<T> found;
            for (int i = 0; i < store.Length; i++)
            {
                if ((found = Interlocked.Exchange(ref store[i], null)) != null)
                {
                    found.Reset(stateOrCompletionSource, count);
                    return found;
                }
            }
            IncrementAllocationCount();
            
            return new MultyResultBox<T>(stateOrCompletionSource, count);
        }

        public static void UnwrapAndRecycle(MultyResultBox<T> box, bool recycle, out ConcurrentDictionary<Message, T> values, out Exception exception)
        {
            if (box == null)
            {
                values = null;
                exception = null;
            }
            else
            {
                values = box.values;
                exception = box.exception;
                box.values = null;
                box.exception = null;
                box.countNeeded = 0;
                box.countDone = -1;
                if (recycle)
                {
                    for (int i = 0; i < store.Length; i++)
                    {
                        if (Interlocked.CompareExchange(ref store[i], box, null) == null) return;
                    }
                }
            }
        }

        public void SetResult(T value, Message message)
        {
            this.values.TryAdd(message, value);
        }

        public override bool TryComplete(bool isAsync)
        {
            if (stateOrCompletionSource is TaskCompletionSource<T[]>)
            {
                var tcs = (TaskCompletionSource<T[]>)stateOrCompletionSource;
#if !PLAT_SAFE_CONTINUATIONS // we don't need to check in this scenario
                if (isAsync || TaskSource.IsSyncSafe(tcs.Task))
#endif
                {
                    var cntDone = Interlocked.Increment(ref this.countDone);
                    if (cntDone != countNeeded)
                    {
                        return true;
                    }
                    
                    UnwrapAndRecycle(this, true, out var val, out var ex);

                    if (ex == null) tcs.TrySetResult(val.Values.ToArray());
                    else
                    {
                        if (ex is TaskCanceledException) tcs.TrySetCanceled();
                        else tcs.TrySetException(ex);
                        // mark it as observed
                        GC.KeepAlive(tcs.Task.Exception);
                        GC.SuppressFinalize(tcs.Task);
                    }
                    return true;
                }
#if !PLAT_SAFE_CONTINUATIONS
                else
                { // looks like continuations; push to async to preserve the reader thread
                    return false;
                }
#endif
            }
            else
            {
                lock (this)
                { // tell the waiting thread that we're done
                    Monitor.PulseAll(this);
                }
                ConnectionMultiplexer.TraceWithoutContext("Pulsed", "Result");
                return true;
            }
        }

        private void Reset(object stateOrCompletionSource, int count)
        {
            values = new ConcurrentDictionary<Message, T>();
            exception = null;
            countNeeded = count;
            countDone = 0;

            this.stateOrCompletionSource = stateOrCompletionSource;
        }
    }
    sealed class ResultBox<T> : ResultBox
    {
        private static readonly ResultBox<T>[] store = new ResultBox<T>[64];

        private object stateOrCompletionSource;

        private T value;

        public ResultBox(object stateOrCompletionSource)
        {
            this.stateOrCompletionSource = stateOrCompletionSource;
        }

        public object AsyncState =>
            stateOrCompletionSource is TaskCompletionSource<T>
                ? ((TaskCompletionSource<T>) stateOrCompletionSource).Task.AsyncState
                : stateOrCompletionSource;

        public static ResultBox<T> Get(object stateOrCompletionSource)
        {
            ResultBox<T> found;
            for (int i = 0; i < store.Length; i++)
            {
                if ((found = Interlocked.Exchange(ref store[i], null)) != null)
                {
                    found.Reset(stateOrCompletionSource);
                    return found;
                }
            }
            IncrementAllocationCount();
            
            return new ResultBox<T>(stateOrCompletionSource);
        }

        public static void UnwrapAndRecycle(ResultBox<T> box, bool recycle, out T value, out Exception exception)
        {
            if (box == null)
            {
                value = default(T);
                exception = null;
            }
            else
            {
                value = box.value;
                exception = box.exception;
                box.value = default(T);
                box.exception = null;
                if (recycle)
                {
                    for (int i = 0; i < store.Length; i++)
                    {
                        if (Interlocked.CompareExchange(ref store[i], box, null) == null) return;
                    }
                }
            }
        }

        public void SetResult(T value)
        {
            this.value = value;
        }

        public override bool TryComplete(bool isAsync)
        {
            if (stateOrCompletionSource is TaskCompletionSource<T>)
            {
                var tcs = (TaskCompletionSource<T>)stateOrCompletionSource;
#if !PLAT_SAFE_CONTINUATIONS // we don't need to check in this scenario
                if (isAsync || TaskSource.IsSyncSafe(tcs.Task))
#endif
                {
                    T val;
                    Exception ex;
                    UnwrapAndRecycle(this, true, out val, out ex);

                    if (ex == null) tcs.TrySetResult(val);
                    else
                    {
                        if (ex is TaskCanceledException) tcs.TrySetCanceled();
                        else tcs.TrySetException(ex);
                        // mark it as observed
                        GC.KeepAlive(tcs.Task.Exception);
                        GC.SuppressFinalize(tcs.Task);
                    }
                    return true;
                }
#if !PLAT_SAFE_CONTINUATIONS
                else
                { // looks like continuations; push to async to preserve the reader thread
                    return false;
                }
#endif
            }
            else
            {
                lock (this)
                { // tell the waiting thread that we're done
                    Monitor.PulseAll(this);
                }
                ConnectionMultiplexer.TraceWithoutContext("Pulsed", "Result");
                return true;
            }
        }

        private void Reset(object stateOrCompletionSource)
        {
            value = default(T);
            exception = null;

            this.stateOrCompletionSource = stateOrCompletionSource;
        }
    }

}
