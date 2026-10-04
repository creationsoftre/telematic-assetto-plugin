using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace Telematic.AssettoServer.Plugin;

public sealed class TelematicModule : AssettoServerModule<TelematicConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<ClientWebSocketTransport>().As<IGatewayTransport>().SingleInstance();
        builder.RegisterType<VehicleStateCache>().AsSelf().SingleInstance();
        builder.RegisterType<TelematicGatewayClient>().AsSelf().SingleInstance();
        builder.RegisterType<AssettoEventBridge>().AsSelf().SingleInstance();
        builder.RegisterType<TelematicPluginService>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<TelemetrySampler>().AsSelf().As<IHostedService>().SingleInstance();
    }
}
