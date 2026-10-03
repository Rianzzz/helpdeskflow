using System.Text.Json;
using HelpDeskFlow.Contracts;
using HelpDeskFlow.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;

namespace HelpDeskFlow.Messaging.Outbox;

/// <summary>
/// Não fala com o RabbitMQ. Apenas adiciona o evento ao DbContext da requisição; quem confirma é o SaveChanges
/// do chamador, na mesma transação do dado de negócio. Isso elimina o problema do "dual write".
/// </summary>
public sealed class OutboxEventPublisher<TContext>(TContext db) : IEventPublisher
    where TContext : DbContext
{
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : IIntegrationEvent
    {
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = @event.EventId,
            Type = TEvent.EventName,
            Payload = JsonSerializer.Serialize(@event, MessageSerializer.Options),
            OccurredAt = @event.OccurredAt,
            CreatedAt = DateTime.UtcNow,
            TraceParent = System.Diagnostics.Activity.Current?.Id // liga o trace da requisição ao envio assíncrono
        });
        return Task.CompletedTask;
    }
}
