using Identity.Application;
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
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        services.AddSingleton<Application.IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITokenService, TokenService>();
        services.AddScoped<AuthService>();
        return services;
    }
}
