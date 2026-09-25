using DeveAgentManager.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeveAgentManager.Providers.Proxmox;

public static class ProxmoxServiceCollectionExtensions
{
    public static IServiceCollection AddProxmoxProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ProxmoxOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.AddHttpClient(ProxmoxProvider.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(2))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Proxmox ships with a self-signed certificate; PROXMOX_VERIFY_TLS=false accepts it.
                ServerCertificateCustomValidationCallback = options.VerifyTls ? null : HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
        services.AddSingleton<IRunnerProvider, ProxmoxProvider>();
        return services;
    }
}
