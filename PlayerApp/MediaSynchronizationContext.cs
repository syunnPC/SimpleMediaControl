using System;
using System.Threading;

namespace PlayerApp;

internal sealed class MediaSynchronizationContext(Func<Action, bool> enqueue) : SynchronizationContext
{
    public override SynchronizationContext CreateCopy() => this;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (!enqueue(() =>
        {
            // A bare DispatcherQueue does not install a SynchronizationContext for
            // each callback. Restore it on every continuation, including after WinRT
            // awaits, so later awaits cannot move native work onto the thread pool.
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }))
        {
            throw new InvalidOperationException("The media dispatcher has stopped.");
        }
    }
}
