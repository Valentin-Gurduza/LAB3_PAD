using System.Collections.Concurrent;
using System.Net;
using Ocelot.Configuration;
using Ocelot.Provider.Polly.Interfaces;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Warehouse.Gateway;

/// <summary>
/// Builds a route-level Polly v8 resilience pipeline whose inner TimeoutStrategy fires
/// slightly before Ocelot's outer TimeoutDelegatingHandler so downstream timeouts,
/// connection failures, and 5xx responses all count toward opening the Circuit Breaker.
/// </summary>
public sealed class GatewayResiliencePipelineProvider : IPollyQoSResiliencePipelineProvider<HttpResponseMessage>
{
    private readonly ConcurrentDictionary<string, ResiliencePipeline<HttpResponseMessage>> _pipelines = new();
    private readonly ILogger<GatewayResiliencePipelineProvider> _logger;

    public GatewayResiliencePipelineProvider(ILogger<GatewayResiliencePipelineProvider> logger)
    {
        _logger = logger;
    }

    public ResiliencePipeline<HttpResponseMessage> GetResiliencePipeline(DownstreamRoute route)
    {
        string key = $"{route.ServiceName}:{route.UpstreamPathTemplate?.OriginalValue}";
        return _pipelines.GetOrAdd(key, _ => BuildPipeline(key, route.QosOptions));
    }

    private ResiliencePipeline<HttpResponseMessage> BuildPipeline(string key, QoSOptions qos)
    {
        int minThroughput = Math.Max(2, qos.ExceptionsAllowedBeforeBreaking);
        TimeSpan breakDuration = TimeSpan.FromMilliseconds(Math.Max(500, qos.DurationOfBreak));
        // Fire 400ms before Ocelot's outer TimeoutDelegatingHandler (which also reads qos.TimeoutValue)
        int innerTimeoutMs = qos.TimeoutValue > 1400 ? qos.TimeoutValue - 400 : Math.Max(1000, qos.TimeoutValue);

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = minThroughput,
                BreakDuration = breakDuration,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TimeoutException>()
                    .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError),
                OnOpened = args =>
                {
                    _logger.LogWarning("Circuit Breaker DESCHIS pe ruta '{RouteKey}' pentru {BreakSec}s.", key, args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    _logger.LogInformation("Circuit Breaker ÎNCHIS (recuperat) pe ruta '{RouteKey}'.", key);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromMilliseconds(innerTimeoutMs)
            })
            .Build();
    }
}
