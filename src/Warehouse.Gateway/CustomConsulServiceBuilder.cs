using Consul;
using Ocelot.Logging;
using Ocelot.Provider.Consul;
using Ocelot.Provider.Consul.Interfaces;

namespace Warehouse.Gateway;

public class CustomConsulServiceBuilder : DefaultConsulServiceBuilder
{
    public CustomConsulServiceBuilder(
        Func<ConsulRegistryConfiguration> configurationFactory,
        IConsulClientFactory clientFactory,
        IOcelotLoggerFactory loggerFactory)
        : base(configurationFactory, clientFactory, loggerFactory)
    {
    }

    protected override string GetDownstreamHost(ServiceEntry entry, Node node) =>
        !string.IsNullOrWhiteSpace(entry.Service.Address) ? entry.Service.Address : base.GetDownstreamHost(entry, node);
}
