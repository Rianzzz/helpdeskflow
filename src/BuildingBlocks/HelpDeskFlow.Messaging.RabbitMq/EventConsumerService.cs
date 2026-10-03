using System.Text.Json;
using HelpDeskFlow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace HelpDeskFlow.Messaging.RabbitMq;

/// <summary>
/// Consome UM tipo de evento de UMA fila própria do serviço ("tickets.identity.user-registered").
/// Cada serviço tem a sua fila, então dois serviços interessados no mesmo evento recebem cópias independentes.
/// </summary>
public sealed class EventConsumerService<TEvent>(
    RabbitConnection connection,
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<EventConsumerService<TEvent>> logger) : BackgroundService
    where TEvent : IIntegrationEvent
{
    // Mais tentativas, com espera crescente (0,5 s, 1 s, 2 s, 4 s = ~7,5 s): cobre a "defasagem" normal entre
    // eventos de serviços diferentes (consistência eventual) antes de desistir e mandar para a DLQ.
    private const int MaxAttempts = 5;
    private readonly string _queue = $"{options.Value.ServiceName}.{TEvent.EventName}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Se o RabbitMQ ainda não estiver no ar, o serviço NÃO cai: tenta de novo até conseguir.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartConsumingAsync(stoppingToken);
                return; // o consumo continua em callbacks; o canal reconecta sozinho (auto recovery)
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Não consegui iniciar o consumidor {Queue}. Nova tentativa em 5s.", _queue);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task StartConsumingAsync(CancellationToken ct)
    {
        var conn = await connection.GetAsync(ct);
        var channel = await conn.CreateChannelAsync(cancellationToken: ct);

        // Topologia (idempotente: declarar de novo algo que já existe igual não faz nada).
        await channel.ExchangeDeclareAsync(Topology.EventsExchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(Topology.DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        var deadLetterQueue = $"{_queue}.dlq";
        await channel.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(deadLetterQueue, Topology.DeadLetterExchange, _queue, cancellationToken: ct);

        await channel.QueueDeclareAsync(_queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                // Se uma mensagem for rejeitada (nack sem requeue), o RabbitMQ a move para a DLQ.
                ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = _queue
            }, cancellationToken: ct);
        await channel.QueueBindAsync(_queue, Topology.EventsExchange, TEvent.EventName, cancellationToken: ct);

        // Processa no máximo 10 mensagens por vez (evita sobrecarregar o serviço).
        await channel.BasicQosAsync(0, 10, false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => OnMessageAsync(channel, ea);
        await channel.BasicConsumeAsync(_queue, autoAck: false, consumer, ct);

        logger.LogInformation("Consumindo {Queue}", _queue);
    }

    private async Task OnMessageAsync(IChannel channel, BasicDeliverEventArgs ea)
    {
        TEvent? @event;
        try
        {
            @event = JsonSerializer.Deserialize<TEvent>(ea.Body.Span, MessageSerializer.Options);
            if (@event is null) throw new JsonException("Mensagem vazia.");
        }
        catch (JsonException ex)
        {
            // "Mensagem veneno": tentar de novo nunca vai funcionar. Vai direto para a DLQ.
            logger.LogError(ex, "Mensagem inválida em {Queue}; enviada para a DLQ.", _queue);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<TEvent>>();
                await handler.HandleAsync(@event, CancellationToken.None);

                // ACK só DEPOIS de processar: se o serviço cair no meio, o RabbitMQ reentrega.
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao processar {EventId} em {Queue} (tentativa {Attempt}/{Max})",
                    @event.EventId, _queue, attempt, MaxAttempts);

                if (attempt < MaxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1))); // 0,5s, 1s...
            }
        }

        logger.LogError("Evento {EventId} esgotou as tentativas em {Queue}; enviado para a DLQ.", @event.EventId, _queue);
        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
    }
}
