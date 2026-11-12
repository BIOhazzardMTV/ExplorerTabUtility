using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerTabUtility.Helpers;

public sealed class StaTaskScheduler : TaskScheduler, IDisposable
{
    private readonly Thread _staThread;
    private readonly BlockingCollection<Task> _tasks = new();

    public StaTaskScheduler()
    {
        _staThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "STA Thread"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void Run()
    {
        try
        {
            foreach (var task in _tasks.GetConsumingEnumerable())
            {
                TryExecuteTask(task);
            }
        }
        finally
        {
            lock (_tasks) _tasks.Dispose();
        }
    }

    // Called by the TPL to queue a task
    protected override void QueueTask(Task task)
    {
        _tasks.Add(task);
    }

    // (Optional) TPL calls this to see if we can run inline
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        // Since this scheduler only wants tasks on its dedicated thread, we *generally*
        // disallow inlining unless this is the STA thread itself.
        if (Thread.CurrentThread == _staThread)
        {
            return TryExecuteTask(task);
        }
        return false;
    }

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        return _tasks.ToArray();
    }

    public void Dispose()
    {
        // A pending Shell call can need the UI thread while it handles shutdown.
        lock (_tasks) _tasks.CompleteAdding();
    }
}
