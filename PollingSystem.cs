using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MsgPollingSystem;

/// <summary>
/// A message-based request polling service.
/// </summary>
public interface IMessageService<TParam, TResult>
{
    Task<TResult> SubmitAsync(TParam data, CancellationToken cancellationToken = default);
    void Start();
    void Stop();
}

/// <summary>
/// Synchronous request executor.
/// </summary>
public interface IDriver<TParam, TResult>
{
    TResult Execute(TParam data);
}

/// <summary>
/// Asynchronous request executor.
/// </summary>
public interface IAsyncDriver<TParam, TResult> : IDriver<TParam, TResult>
{
    Task<TResult> ExecuteAsync(TParam data);
}

internal sealed class Request<TParam, TResult>
{
    public TParam Data { get; }
    public CancellationToken CancellationToken { get; }
    public TaskCompletionSource<TResult> Completion { get; }

    public Request(TParam data, CancellationToken cancellationToken)
    {
        this.Data = data;
        this.CancellationToken = cancellationToken;
        this.Completion = new TaskCompletionSource<TResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// High-performance, non-busy-loop message service using Channels.
/// </summary>
public sealed class ChannelMessageService<TParam, TResult>
    : IMessageService<TParam, TResult>
{
    private readonly Channel<Request<TParam, TResult>> _channel;
    private readonly Func<TParam, Task<TResult>> _executor;
    private readonly CancellationTokenSource _cts;
    private Task? _worker;

    public ChannelMessageService(IDriver<TParam, TResult> driver)
    {
        this._channel = Channel.CreateUnbounded<Request<TParam, TResult>>();
        this._cts = new CancellationTokenSource();

        if (driver is IAsyncDriver<TParam, TResult> asyncDriver)
        {
            this._executor = asyncDriver.ExecuteAsync;
        }
        else
        {
            this._executor = (data) => Task.FromResult(driver.Execute(data));
        }
    }

    public Task<TResult> SubmitAsync(
        TParam data,
        CancellationToken cancellationToken = default)
    {
        var request = new Request<TParam, TResult>(data, cancellationToken);
        this._channel.Writer.TryWrite(request);
        return request.Completion.Task;
    }

    public void Start()
    {
        var token = this._cts.Token;

        this._worker = Task.Run(async () =>
        {
            await foreach (var request in this._channel.Reader.ReadAllAsync(token))
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled();
                    continue;
                }

                try
                {
                    var result = await this._executor(request.Data);
                    request.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    request.Completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    request.Completion.TrySetException(ex);
                }
            }
        });
    }

    public void Stop()
    {
        this._cts.Cancel();
    }
}

/// <summary>
/// Polling-based implementation. Uses busy looping with optional delay.
/// Prefer ChannelMessageService unless required.
/// </summary>
public sealed class QueueMessageService<TParam, TResult>
    : IMessageService<TParam, TResult>
{
    private readonly ConcurrentQueue<Request<TParam, TResult>> _queue;
    private readonly Func<TParam, Task<TResult>> _executor;
    private readonly CancellationTokenSource _cts;
    private Task? _worker;

    public TimeSpan PollInterval { get; set; } = TimeSpan.Zero;

    public QueueMessageService(IDriver<TParam, TResult> driver)
    {
        this._queue = new ConcurrentQueue<Request<TParam, TResult>>();
        this._cts = new CancellationTokenSource();

        if (driver is IAsyncDriver<TParam, TResult> asyncDriver)
        {
            this._executor = asyncDriver.ExecuteAsync;
        }
        else
        {
            this._executor = (data) => Task.FromResult(driver.Execute(data));
        }
    }

    public Task<TResult> SubmitAsync(
        TParam data,
        CancellationToken cancellationToken = default)
    {
        var request = new Request<TParam, TResult>(data, cancellationToken);
        this._queue.Enqueue(request);
        return request.Completion.Task;
    }

    public void Start()
    {
        var token = this._cts.Token;

        this._worker = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                while (this._queue.TryDequeue(out var request))
                {
                    if (request.CancellationToken.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled();
                        continue;
                    }

                    try
                    {
                        var result = await this._executor(request.Data);
                        request.Completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException)
                    {
                        request.Completion.TrySetCanceled();
                    }
                    catch (Exception ex)
                    {
                        request.Completion.TrySetException(ex);
                    }
                }

                if (this.PollInterval > TimeSpan.Zero)
                {
                    await Task.Delay(this.PollInterval, token);
                }
                else
                {
                    await Task.Yield();
                }
            }
        });
    }

    public void Stop()
    {
        this._cts.Cancel();
    }
}




