using HelpDeskFlow.Contracts;
using HelpDeskFlow.Messaging.Outbox;
using HelpDeskFlow.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tickets.Application;
using Tickets.Application.Abstractions;
using Tickets.Infrastructure.Messaging;
using Tickets.Infrastructure.Persistence;

namespace Tickets.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTicketsServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<TicketsDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("TicketsDb"))
             .UseSnakeCaseNamingConvention());
        services.AddOutbox<TicketsDbContext>(); // eventos saem pela tabela outbox_messages
        services.AddScoped<ITicketRepository, TicketRepository>();
        services.AddScoped<IKnownUserRepository, KnownUserRepository>();
        services.AddScoped<TicketService>();

        // Eventos que o Tickets consome.
        services.AddEventHandler<UserRegistered, UserRegisteredHandler>();
        return services;
    }
}
