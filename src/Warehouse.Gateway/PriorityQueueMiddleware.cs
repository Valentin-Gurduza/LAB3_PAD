using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Warehouse.Gateway;

public class PriorityOptions
{
    public int MaxConcurrent { get; set; } = 2;
}

public class PriorityQueueMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _maxConcurrent;
    private readonly object _sync = new();
    private readonly PriorityQueue<TaskCompletionSource<bool>, (int Rank, long Seq)> _queue = new();
    private int _active;
    private long _sequence;

    public PriorityQueueMiddleware(RequestDelegate next, PriorityOptions options)
    {
        _next = next;
        _maxConcurrent = Math.Max(1, options.MaxConcurrent);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var (normalized, rank) = ParsePriority(context.Request);
        var waitSw = Stopwatch.StartNew();

        TaskCompletionSource<bool>? waiter = null;
        lock (_sync)
        {
            if (_active < _maxConcurrent)
            {
                _active++;
            }
            else
            {
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _queue.Enqueue(waiter, (rank, _sequence++));
            }
        }

        if (waiter is not null)
        {
            try
            {
                await waiter.Task.WaitAsync(context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                if (!waiter.TrySetCanceled(context.RequestAborted))
                    ReleaseOrPassBaton();
                throw;
            }
        }

        waitSw.Stop();
        string waitMs = waitSw.ElapsedMilliseconds.ToString();

        context.Response.Headers["X-Priority-Applied"] = normalized;
        context.Response.Headers["X-Queue-Wait-Ms"] = waitMs;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Priority-Applied"] = normalized;
            context.Response.Headers["X-Queue-Wait-Ms"] = waitMs;
            return Task.CompletedTask;
        });

        try
        {
            await _next(context);
        }
        finally
        {
            ReleaseOrPassBaton();
        }
    }

    private void ReleaseOrPassBaton()
    {
        while (true)
        {
            TaskCompletionSource<bool>? candidate = null;
            lock (_sync)
            {
                while (_queue.TryDequeue(out var dequeued, out _))
                {
                    if (!dequeued.Task.IsCompleted)
                    {
                        candidate = dequeued;
                        break;
                    }
                }

                if (candidate is null)
                {
                    _active--;
                    return;
                }
            }

            if (candidate.TrySetResult(true))
                return;
        }
    }

    private static (string Name, int Rank) ParsePriority(HttpRequest request)
    {
        string raw = string.Empty;
        if (request.Headers.TryGetValue("X-Priority", out var headerVal) && !string.IsNullOrWhiteSpace(headerVal))
            raw = headerVal.ToString().Trim();
        else if (request.Query.TryGetValue("priority", out var queryVal) && !string.IsNullOrWhiteSpace(queryVal))
            raw = queryVal.ToString().Trim();

        return raw.ToLowerInvariant() switch
        {
            "high" => ("high", 0),
            "low" => ("low", 2),
            _ => ("normal", 1)
        };
    }
}
