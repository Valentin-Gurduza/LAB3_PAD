using Consul;
using Warehouse.Node.Models;

namespace Warehouse.Node.Services;

public class ConsulRegistrationHostedService : IHostedService
{
    private readonly IConsulClient _consul;
    private readonly NodeOptions _options;
    private readonly ILogger<ConsulRegistrationHostedService> _logger;

    public ConsulRegistrationHostedService(
        IConsulClient consul,
        NodeOptions options,
        ILogger<ConsulRegistrationHostedService> logger)
    {
        _consul = consul;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableConsul)
            return;

        var registration = new AgentServiceRegistration
        {
            ID = _options.NodeId,
            Name = "warehouse-node",
            Address = _options.ServiceHost,
            Port = _options.ServicePort,
            Tags = [$"shard:{_options.ShardId}", $"node:{_options.NodeId}"],
            Check = new AgentServiceCheck
            {
                HTTP = $"http://{_options.ServiceHost}:{_options.ServicePort}/health",
                Interval = TimeSpan.FromSeconds(5),
                Timeout = TimeSpan.FromSeconds(2),
                DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(30)
            }
        };

        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await _consul.Agent.ServiceDeregister(_options.NodeId, cancellationToken);
                await _consul.Agent.ServiceRegister(registration, cancellationToken);
                _logger.LogInformation("Registered {NodeId} (shard {ShardId}) in Consul.", _options.NodeId, _options.ShardId);
                return;
            }
            catch (Exception ex) when (attempt < 10)
            {
                _logger.LogWarning(ex, "Consul registration attempt {Attempt}/10 failed; retrying...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableConsul)
            return;

        try
        {
            await _consul.Agent.ServiceDeregister(_options.NodeId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deregister {NodeId} from Consul.", _options.NodeId);
        }
    }
}
