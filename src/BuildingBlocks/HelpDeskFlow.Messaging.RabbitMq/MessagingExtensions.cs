using HelpDeskFlow.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HelpDeskFlow.Messaging.RabbitMq;

public static class MessagingExtensions
{
    /// <summary>Registra a conexão e o publicador. Chame uma vez por serviço.</summary>
    public static IServiceCollection AddRabbitMessaging(
        this IServiceCollection services, IConfiguration config, string serviceName)
    {
        services.Configure<MessagingOptions>(o =>
        {
            config.GetSection(MessagingOptions.SectionName).Bind(o);
            o.ServiceName = serviceName;
        });

        services.AddSingleton<RabbitConnection>();
        services.AddSingleton<RabbitEventPublisher>();
        services.AddSingleton<IRawEventPublisher>(sp => sp.GetRequiredService<RabbitEventPublisher>());

        // Padrão: publicação direta. Serviços com Outbox substituem isto (AddOutbox), em qualquer ordem de registro.
        services.TryAddSingleton<IEventPublisher>(sp => sp.GetRequiredService<RabbitEventPublisher>());
        return services;
    }

    /// <summary>Declara que este serviço reage ao evento <typeparamref name="TEvent"/> usando o handler informado.</summary>
    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IIntegrationEvent
        where THandler : class, IEventHandler<TEvent>
    {
        services.AddScoped<IEventHandler<TEvent>, THandler>();
        services.AddSingleton<IHostedService, EventConsumerService<TEvent>>();
        return services;
    }
}
