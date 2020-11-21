﻿/*
 * Copyright 2015-2016 Roger Alsing, Aaron Stannard, Jeff Cyr
 * Helios.DedicatedThreadPool - https://github.com/helios-io/DedicatedThreadPool
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Helios.Concurrency
{
    /// <summary>
    /// The type of threads to use - either foreground or background threads.
    /// </summary>
    internal enum ThreadType
    {
        Foreground,
        Background
    }

    /// <summary>
    /// Provides settings for a dedicated thread pool
    /// </summary>
    internal sealed class DedicatedThreadPoolSettings
    {
        /// <summary>
        /// Background threads are the default thread type
        /// </summary>
        public const ThreadType DefaultThreadType = ThreadType.Background;

        public DedicatedThreadPoolSettings(int numThreads, string name = null, TimeSpan? deadlockTimeout = null, Action<Exception> exceptionHandler = null)
            : this(numThreads, DefaultThreadType, name, deadlockTimeout, exceptionHandler)
        { }

        public DedicatedThreadPoolSettings(int numThreads, ThreadType threadType, string name = null, TimeSpan? deadlockTimeout = null, Action<Exception> exceptionHandler = null)
        {
            Name = name ?? ("DedicatedThreadPool-" + Guid.NewGuid());
            ThreadType = threadType;
            NumThreads = numThreads;
            MinThreads = Math.Min(2, numThreads);
            MaxThreads = Math.Max(numThreads, Math.Max(2, Environment.ProcessorCount-1)); //todo find core count
            DeadlockTimeout = deadlockTimeout;
            ExceptionHandler = exceptionHandler ?? (ex => { });

            if (deadlockTimeout.HasValue && deadlockTimeout.Value.TotalMilliseconds <= 0)
                throw new ArgumentOutOfRangeException("deadlockTimeout", string.Format("deadlockTimeout must be null or at least 1ms. Was {0}.", deadlockTimeout));
            if (numThreads <= 0)
                throw new ArgumentOutOfRangeException("numThreads", string.Format("numThreads must be at least 1. Was {0}", numThreads));
        }

        /// <summary>
        /// The total number of threads to run in this thread pool.
        /// </summary>
        public int NumThreads { get; private set; }

        /// <summary>
        /// The min number of threads to run in this thread pool.
        /// Zero thread count is supported
        /// </summary>
        public int MinThreads { get; private set; }

        /// <summary>
        /// The max number of threads to run in this thread pool.
        /// </summary>
        public int MaxThreads { get; private set; }

        /// <summary>
        /// The type of threads to run in this thread pool.
        /// </summary>
        public ThreadType ThreadType { get; private set; }

        /// <summary>
        /// Interval to check for thread deadlocks.
        ///
        /// If a thread takes longer than <see cref="DeadlockTimeout"/> it will be aborted
        /// and replaced.
        /// </summary>
        public TimeSpan? DeadlockTimeout { get; private set; }

        /// <summary>
        /// TBD
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// TBD
        /// </summary>
        public Action<Exception> ExceptionHandler { get; private set; }

        /// <summary>
        /// The type of threads to run in this thread pool.
        /// </summary>
        public bool AllowSynchronousContinuations { get; private set; } = true;

        /// <summary>
        /// QueueUserWorkItem is sychronous called 
        /// </summary>
        public bool SynchronousScheduler { get; private set; } = true;
    }

    /// <summary>
    /// TaskScheduler for working with a <see cref="DedicatedThreadPool"/> instance
    /// </summary>
    internal sealed class DedicatedThreadPoolTaskScheduler : TaskScheduler
    {
        // Indicates whether the current thread is processing work items.
        [ThreadStatic]
        private static bool _currentThreadIsRunningTasks;

        /// <summary>
        /// Number of workers currently running
        /// </summary>
        private int _parallelWorkers = 0; //synced by _tasks lock

        /// <summary>
        /// Number of tasks currently waiting
        /// </summary>
        private int _waitingWork = 0; //estimated, synced by _tasks lock

        private readonly LinkedList<Task> _tasks = new LinkedList<Task>();

        private readonly DedicatedThreadPool _pool;  //queue work is synced by _tasks lock

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="pool">TBD</param>
        public DedicatedThreadPoolTaskScheduler(DedicatedThreadPool pool)
        {
            _pool = pool;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="task">TBD</param>
        protected override void QueueTask(Task task)
        {
            lock (_tasks)
            {
                _tasks.AddLast(task);
                _waitingWork++;

                //request new worker
                if (_parallelWorkers < _pool.Settings.MaxThreads)
                {
                    _parallelWorkers++;
                    RequestWorker();
                }
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="task">TBD</param>
        /// <param name="taskWasPreviouslyQueued">TBD</param>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            //current thread isn't running any tasks, can't execute inline
            if (!_currentThreadIsRunningTasks) return false;

            //remove the task from the queue if it was previously added
            return taskWasPreviouslyQueued
                ? TryDequeue(task) && TryExecuteTask(task)
                : TryExecuteTask(task);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="task">TBD</param>
        /// <returns>TBD</returns>
        protected override bool TryDequeue(Task task)
        {
            lock (_tasks)
            {
                if (_tasks.Remove(task))
                {
                    _waitingWork--;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Level of concurrency is max number of threads
        /// in the <see cref="DedicatedThreadPool"/>.
        /// </summary>
        public override int MaximumConcurrencyLevel
        {
            get { return _pool.Settings.MaxThreads; }
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// This exception is thrown if can't ensure a thread-safe return of the list of tasks.
        /// </exception>
        /// <returns>TBD</returns>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            var lockTaken = false;
            try
            {
                Monitor.TryEnter(_tasks, ref lockTaken);

                //should this be immutable?
                return lockTaken ? _tasks : throw new NotSupportedException();
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_tasks);
            }
        }

        private void RequestWorker()
        {
            _pool.QueueUserWorkItem(() =>
            {
                // this thread is now available for inlining
                _currentThreadIsRunningTasks = true;
                try
                {
                    // Process all available items in the queue.
                    while (true)
                    {
                        Task item;
                        lock (_tasks)
                        {
                            // done processing
                            if (_tasks.Count == 0)
                            {
                                _parallelWorkers--;
                                break;
                            }

                            // Get the next item from the queue
                            item = _tasks.First.Value;
                            _tasks.RemoveFirst();
                            _waitingWork--;
                        }

                        // Execute the task we pulled out of the queue
                        TryExecuteTask(item);
                    }
                }
                // We're done processing items on the current thread
                finally { _currentThreadIsRunningTasks = false; }
            });
        }
    }



    /// <summary>
    /// An instanced, dedicated thread pool.
    /// </summary>
    internal sealed class DedicatedThreadPool : IDisposable
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="settings">TBD</param>
        public DedicatedThreadPool(DedicatedThreadPoolSettings settings)
        {
            //_workQueue = new ThreadPoolWorkQueue();
            _workChannel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = settings.AllowSynchronousContinuations, //todo check what continuations is syncron
                SingleReader = settings.NumThreads <= 1 && settings.MaxThreads <= 1,
                SingleWriter = settings.SynchronousScheduler
            });
            Settings = settings;
            NumThreads = settings.NumThreads;
            _workers = Enumerable.Range(1, NumThreads).Select(workerId => new PoolWorker(this, workerId)).ToArray();

            // Note:
            // The DedicatedThreadPoolSupervisor was removed because aborting thread could lead to unexpected behavior
            // If a new implementation is done, it should spawn a new thread when a worker is not making progress and
            // try to keep {settings.NumThreads} active threads.
        }

        /// <summary>
        /// TBD
        /// </summary>
        public DedicatedThreadPoolSettings Settings { get; private set; }

        /// <summary>
        /// active thread count
        /// </summary>
        public int NumThreads { get; private set; }

        //private readonly ThreadPoolWorkQueue _workQueue;
        private readonly Channel<Action> _workChannel;
        private PoolWorker[] _workers;

        private int _cleanCounter = 0;

        /// <summary>
        /// TBD
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// This exception is thrown if the given <paramref name="work"/> item is undefined.
        /// </exception>
        /// <returns>TBD</returns>
        public bool QueueUserWorkItem(Action work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work), "Work item cannot be null.");

            if(_workChannel.Writer.TryWrite(work))
            {
                if(Settings.SynchronousScheduler && _cleanCounter++ % 50 == 0)
                {
                    //todo asyncronous worker management

                    _cleanCounter = 0;

                    var stoppable = Math.Max(0, NumThreads - Settings.MinThreads);
                    var running = 0;

                    //cleanup workers
                    for (int i = 0; i < _workers.Length; i++)
                    {
                        var idle = _workers[i].Idle;                        
                        if (idle == -1) //completed
                        {
                            NumThreads--;
                            stoppable = Math.Max(0, stoppable - 1);
                        } 
                        else if(stoppable > 0 && idle > 75)
                        {
                            _workers[i].Stop();
                            stoppable--;
                        } 
                        else if(idle < 10)
                        {
                            running++;
                        }
                    }

                    //increase workers
                    if(NumThreads < Settings.MinThreads || (running == NumThreads && NumThreads < Settings.MaxThreads))
                    {
                        NumThreads++;
                        if (_workers.Length < NumThreads)
                            Array.Resize(ref _workers, NumThreads);

                        //recreate worker
                        for (int i = 0; i < _workers.Length; i++)
                        {
                            if (_workers[i] is null || _workers[i].Idle == -1)
                                _workers[i] = new PoolWorker(this, i);
                        }
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// TBD
        /// </summary>
        public void Dispose()
        {
            _workChannel.Writer.Complete();
        }

        /// <summary>
        /// TBD
        /// </summary>
        public void WaitForThreadsExit()
        {
            WaitForThreadsExit(Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="timeout">TBD</param>
        public void WaitForThreadsExit(TimeSpan timeout)
        {
            Task.WaitAll(_workers.Select(worker => worker.ThreadExit).ToArray(), timeout);
        }

        #region Pool worker implementation

        private sealed class PoolWorker
        {
            private readonly DedicatedThreadPool _pool;

            private readonly TaskCompletionSource<object> _threadExit;

            private bool _stop;

            public Task ThreadExit
            {
                get { return _threadExit.Task; }
            }

            public int Idle { get; private set; } = 10;

            public PoolWorker(DedicatedThreadPool pool, int workerId)
            {
                _pool = pool;
                _threadExit = new TaskCompletionSource<object>();

                var thread = new Thread(RunThread)
                {
                    IsBackground = pool.Settings.ThreadType == ThreadType.Background,
                };

                if (pool.Settings.Name != null)
                    thread.Name = string.Format("{0}_{1}", pool.Settings.Name, workerId);

                thread.Start();
            }

            public void Stop()
            {
                _stop = true;
            }

            private void RunThread()
            {
                try
                {
                    var reader = _pool._workChannel.Reader;

                    while (!_stop)
                    {
                        if (reader.TryRead(out var action))
                        {
                            Idle = Math.Max(0, Idle-1);

                            try
                            {
                                action();
                            }
                            catch (Exception ex)
                            {
                                _pool.Settings.ExceptionHandler(ex);
                            }
                        }
                        else
                        {
                            Idle = Math.Min(Idle+2, 100);

                            var t = reader.WaitToReadAsync();
                            if (t.IsCompleted ? !t.Result : !t.AsTask().GetAwaiter().GetResult())
                                return; //completed
                        }
                    }
                }
                finally
                {
                    Idle = -1;
                    _threadExit.TrySetResult(null);
                }
            }
        }

        #endregion

        #region WorkQueue implementation

        [Obsolete]
        private class ThreadPoolWorkQueue
        {
            private static readonly int ProcessorCount = Environment.ProcessorCount;
            private const int CompletedState = 1;

            private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
            private readonly UnfairSemaphore _semaphore = new UnfairSemaphore();
            private int _outstandingRequests;
            private int _isAddingCompleted;

            public bool IsAddingCompleted
            {
                get { return Volatile.Read(ref _isAddingCompleted) == CompletedState; }
            }

            public bool TryAdd(Action work)
            {
                // If TryAdd returns true, it's guaranteed the work item will be executed.
                // If it returns false, it's also guaranteed the work item won't be executed.

                if (IsAddingCompleted)
                    return false;

                _queue.Enqueue(work);
                EnsureThreadRequested();

                return true;
            }

            public IEnumerable<Action> GetConsumingEnumerable()
            {
                while (true)
                {
                    Action work;
                    if (_queue.TryDequeue(out work))
                    {
                        yield return work;
                    }
                    else if (IsAddingCompleted)
                    {
                        while (_queue.TryDequeue(out work))
                            yield return work;

                        break;
                    }
                    else
                    {
                        _semaphore.Wait();
                        MarkThreadRequestSatisfied();
                    }
                }
            }

            public void CompleteAdding()
            {
                int previousCompleted = Interlocked.Exchange(ref _isAddingCompleted, CompletedState);

                if (previousCompleted == CompletedState)
                    return;

                // When CompleteAdding() is called, we fill up the _outstandingRequests and the semaphore
                // This will ensure that all threads will unblock and try to execute the remaining item in
                // the queue. When IsAddingCompleted is set, all threads will exit once the queue is empty.

                while (true)
                {
                    int count = Volatile.Read(ref _outstandingRequests);
                    int countToRelease = UnfairSemaphore.MaxWorker - count;

                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, UnfairSemaphore.MaxWorker, count);

                    if (prev == count)
                    {
                        _semaphore.Release((short)countToRelease);
                        break;
                    }
                }
            }

            private void EnsureThreadRequested()
            {
                // There is a double counter here (_outstandingRequest and _semaphore)
                // Unfair semaphore does not support value bigger than short.MaxValue,
                // trying to Release more than short.MaxValue could fail miserably.

                // The _outstandingRequest counter ensure that we only request a
                // maximum of {ProcessorCount} to the semaphore.

                // It's also more efficient to have two counter, _outstandingRequests is
                // more lightweight than the semaphore.

                // This trick is borrowed from the .Net ThreadPool
                // https://github.com/dotnet/coreclr/blob/bc146608854d1db9cdbcc0b08029a87754e12b49/src/mscorlib/src/System/Threading/ThreadPool.cs#L568

                int count = Volatile.Read(ref _outstandingRequests);
                while (count < ProcessorCount)
                {
                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, count + 1, count);
                    if (prev == count)
                    {
                        _semaphore.Release();
                        break;
                    }
                    count = prev;
                }
            }

            private void MarkThreadRequestSatisfied()
            {
                int count = Volatile.Read(ref _outstandingRequests);
                while (count > 0)
                {
                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, count - 1, count);
                    if (prev == count)
                    {
                        break;
                    }
                    count = prev;
                }
            }
        }

        #endregion

        #region UnfairSemaphore implementation

        // This class has been translated from:
        // https://github.com/dotnet/coreclr/blob/97433b9d153843492008652ff6b7c3bf4d9ff31c/src/vm/win32threadpool.h#L124

        // UnfairSemaphore is a more scalable semaphore than Semaphore.  It prefers to release threads that have more recently begun waiting,
        // to preserve locality.  Additionally, very recently-waiting threads can be released without an addition kernel transition to unblock
        // them, which reduces latency.
        //
        // UnfairSemaphore is only appropriate in scenarios where the order of unblocking threads is not important, and where threads frequently
        // need to be woken.

        [Obsolete]
        [StructLayout(LayoutKind.Sequential)]
        private sealed class UnfairSemaphore
        {
            public const int MaxWorker = 0x7FFF;

            private static readonly int ProcessorCount = Environment.ProcessorCount;

            // We track everything we care about in a single 64-bit struct to allow us to
            // do CompareExchanges on this for atomic updates.
            [StructLayout(LayoutKind.Explicit)]
            private struct SemaphoreState
            {
                //how many threads are currently spin-waiting for this semaphore?
                [FieldOffset(0)]
                public short Spinners;

                //how much of the semaphore's count is available to spinners?
                [FieldOffset(2)]
                public short CountForSpinners;

                //how many threads are blocked in the OS waiting for this semaphore?
                [FieldOffset(4)]
                public short Waiters;

                //how much count is available to waiters?
                [FieldOffset(6)]
                public short CountForWaiters;

                [FieldOffset(0)]
                public long RawData;
            }

            [StructLayout(LayoutKind.Explicit, Size = 64)]
            private struct CacheLinePadding
            { }

            private readonly Semaphore m_semaphore;

            // padding to ensure we get our own cache line
#pragma warning disable 169
            private readonly CacheLinePadding m_padding1;
            private SemaphoreState m_state;
            private readonly CacheLinePadding m_padding2;
#pragma warning restore 169

            public UnfairSemaphore()
            {
                m_semaphore = new Semaphore(0, short.MaxValue);
            }

            public bool Wait()
            {
                return Wait(Timeout.InfiniteTimeSpan);
            }

            public bool Wait(TimeSpan timeout)
            {
                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    // First, just try to grab some count.
                    if (currentCounts.CountForSpinners > 0)
                    {
                        --newCounts.CountForSpinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        // No count available, become a spinner
                        ++newCounts.Spinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            break;
                    }
                }

                //
                // Now we're a spinner.
                //
                int numSpins = 0;
                const int spinLimitPerProcessor = 50;
                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    if (currentCounts.CountForSpinners > 0)
                    {
                        --newCounts.CountForSpinners;
                        --newCounts.Spinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        double spinnersPerProcessor = (double)currentCounts.Spinners / ProcessorCount;
                        int spinLimit = (int)((spinLimitPerProcessor / spinnersPerProcessor) + 0.5);
                        if (numSpins >= spinLimit)
                        {
                            --newCounts.Spinners;
                            ++newCounts.Waiters;
                            if (TryUpdateState(newCounts, currentCounts))
                                break;
                        }
                        else
                        {
                            //
                            // We yield to other threads using Thread.Sleep(0) rather than the more traditional Thread.Yield().
                            // This is because Thread.Yield() does not yield to threads currently scheduled to run on other
                            // processors.  On a 4-core machine, for example, this means that Thread.Yield() is only ~25% likely
                            // to yield to the correct thread in some scenarios.
                            // Thread.Sleep(0) has the disadvantage of not yielding to lower-priority threads.  However, this is ok because
                            // once we've called this a few times we'll become a "waiter" and wait on the Semaphore, and that will
                            // yield to anything that is runnable.
                            //
                            Thread.Sleep(0);
                            numSpins++;
                        }
                    }
                }

                //
                // Now we're a waiter
                //
                bool waitSucceeded = m_semaphore.WaitOne(timeout);

                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    --newCounts.Waiters;

                    if (waitSucceeded)
                        --newCounts.CountForWaiters;

                    if (TryUpdateState(newCounts, currentCounts))
                        return waitSucceeded;
                }
            }

            public void Release()
            {
                Release(1);
            }

            public void Release(short count)
            {
                while (true)
                {
                    SemaphoreState currentState = GetCurrentState();
                    SemaphoreState newState = currentState;

                    short remainingCount = count;

                    // First, prefer to release existing spinners,
                    // because a) they're hot, and b) we don't need a kernel
                    // transition to release them.
                    short spinnersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Spinners - currentState.CountForSpinners)));
                    newState.CountForSpinners += spinnersToRelease;
                    remainingCount -= spinnersToRelease;

                    // Next, prefer to release existing waiters
                    short waitersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Waiters - currentState.CountForWaiters)));
                    newState.CountForWaiters += waitersToRelease;
                    remainingCount -= waitersToRelease;

                    // Finally, release any future spinners that might come our way
                    newState.CountForSpinners += remainingCount;

                    // Try to commit the transaction
                    if (TryUpdateState(newState, currentState))
                    {
                        // Now we need to release the waiters we promised to release
                        if (waitersToRelease > 0)
                            m_semaphore.Release(waitersToRelease);

                        break;
                    }
                }
            }

            private bool TryUpdateState(SemaphoreState newState, SemaphoreState currentState)
            {
                if (Interlocked.CompareExchange(ref m_state.RawData, newState.RawData, currentState.RawData) == currentState.RawData)
                {
                    Debug.Assert(newState.CountForSpinners <= MaxWorker, "CountForSpinners is greater than MaxWorker");
                    Debug.Assert(newState.CountForSpinners >= 0, "CountForSpinners is lower than zero");
                    Debug.Assert(newState.Spinners <= MaxWorker, "Spinners is greater than MaxWorker");
                    Debug.Assert(newState.Spinners >= 0, "Spinners is lower than zero");
                    Debug.Assert(newState.CountForWaiters <= MaxWorker, "CountForWaiters is greater than MaxWorker");
                    Debug.Assert(newState.CountForWaiters >= 0, "CountForWaiters is lower than zero");
                    Debug.Assert(newState.Waiters <= MaxWorker, "Waiters is greater than MaxWorker");
                    Debug.Assert(newState.Waiters >= 0, "Waiters is lower than zero");
                    Debug.Assert(newState.CountForSpinners + newState.CountForWaiters <= MaxWorker, "CountForSpinners + CountForWaiters is greater than MaxWorker");

                    return true;
                }

                return false;
            }

            private SemaphoreState GetCurrentState()
            {
                // Volatile.Read of a long can get a partial read in x86 but the invalid
                // state will be detected in TryUpdateState with the CompareExchange.

                SemaphoreState state = new SemaphoreState();
                state.RawData = Volatile.Read(ref m_state.RawData);
                return state;
            }
        }

        #endregion
    }
}
