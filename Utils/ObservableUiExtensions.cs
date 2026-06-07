using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using Avalonia.Threading;

namespace LuckyLilliaDesktop.Utils;

public static class ObservableUiExtensions
{
    public static IObservable<T> ObserveOnUiThread<T>(this IObservable<T> source)
    {
        return System.Reactive.Linq.Observable.ObserveOn(source, AvaloniaUiScheduler.Instance);
    }
}

public sealed class AvaloniaUiScheduler : IScheduler
{
    public static AvaloniaUiScheduler Instance { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.Now;

    private AvaloniaUiScheduler()
    {
    }

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        var disposed = new BooleanDisposable();
        Dispatcher.UIThread.Post(() =>
        {
            if (!disposed.IsDisposed)
                action(this, state).Dispose();
        });
        return disposed;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        if (dueTime <= TimeSpan.Zero)
            return Schedule(state, action);

        var disposed = new BooleanDisposable();
        var timer = new DispatcherTimer { Interval = dueTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!disposed.IsDisposed)
                action(this, state).Dispose();
        };
        timer.Start();
        return Disposable.Create(() =>
        {
            disposed.Dispose();
            timer.Stop();
        });
    }

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        return Schedule(state, dueTime - Now, action);
    }
}
