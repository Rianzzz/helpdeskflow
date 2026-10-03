using HelpDeskFlow.Contracts;
using HelpDeskFlow.Messaging.Outbox;
using HelpDeskFlow.Messaging.RabbitMq;
using Identity.Application;
using Identity.Infrastructure.Jobs;
using Identity.Infrastructure.Messaging;
using Identity.Infrastructure.Persistence;
using Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<IdentityDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("IdentityDb")).UseSnakeCaseNamingConvention());

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<IdentityDbContext>());
        services.AddOutbox<IdentityDbContext>(); // eventos saem pela tabela outbox_messages, não direto para o broker
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        services.AddSingleton<Application.IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITokenService, TokenService>();
        services.AddScoped<AuthService>();

        // Saga de onboarding: respostas do serviço Tenants + prazo máximo.
        services.AddEventHandler<TenantProvisioned, TenantProvisionedHandler>();
        services.AddEventHandler<TenantProvisioningFailed, TenantProvisioningFailedHandler>();
        services.AddSingleton(config.GetSection(ProvisioningOptions.SectionName).Get<ProvisioningOptions>() ?? new ProvisioningOptions());
        services.AddHostedService<ProvisioningTimeoutJob>();
        return services;
    }
}
