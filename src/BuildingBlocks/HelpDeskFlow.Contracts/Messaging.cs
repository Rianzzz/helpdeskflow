namespace HelpDeskFlow.Contracts;

/// <summary>
/// Evento de integração: um FATO que já aconteceu ("chamado criado"), publicado para quem quiser reagir.
/// Quem publica não sabe (nem se importa) quem consome. Nomeados no passado de propósito.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Id único do evento. Permite ao consumidor detectar entregas duplicadas (idempotência).</summary>
    Guid EventId { get; }
    DateTime OccurredAt { get; }

    /// <summary>Routing key no RabbitMQ, no formato "servico.fato".</summary>
    static abstract string EventName { get; }
}

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : IIntegrationEvent;
}

/// <summary>
/// Reage a um evento. ATENÇÃO: o RabbitMQ garante entrega "pelo menos uma vez", então o mesmo evento
/// pode chegar duas vezes. Todo handler precisa ser idempotente.
/// </summary>
public interface IEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken ct);
}
