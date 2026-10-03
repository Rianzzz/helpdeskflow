using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tickets.Application;
using Tickets.Application.Abstractions;
using Tickets.Infrastructure.Persistence;

namespace Tickets.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTicketsServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<TicketsDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("TicketsDb"))
             .UseSnakeCaseNamingConvention());
        services.AddScoped<ITicketRepository, TicketRepository>();
        services.AddScoped<TicketService>();
        return services;
    }
}
