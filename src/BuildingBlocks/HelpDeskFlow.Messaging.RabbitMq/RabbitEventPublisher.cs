using System.Text.Json;
using HelpDeskFlow.Contracts;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace HelpDeskFlow.Messaging.RabbitMq;

public static class MessageSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed class RabbitEventPublisher(RabbitConnection connection, ILogger<RabbitEventPublisher> logger)
    : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, MessageSerializer.Options);
        var props = new BasicProperties
        {
            MessageId = @event.EventId.ToString(),
            Type = TEvent.EventName,
            ContentType = "application/json",
            Persistent = true, // a mensagem sobrevive a um restart do RabbitMQ
            Timestamp = new AmqpTimestamp(new DateTimeOffset(@event.OccurredAt).ToUnixTimeSeconds())
        };

        await _lock.WaitAsync(ct);
        try
        {
            var channel = await GetChannelAsync(ct);

            // Com "publisher confirms", este await só termina quando o broker CONFIRMA que recebeu e gravou
            // a mensagem. Sem isso, "publicou" não significaria "o RabbitMQ realmente tem a mensagem".
            await channel.BasicPublishAsync(Topology.EventsExchange, TEvent.EventName, mandatory: false, props, body, ct);
            logger.LogInformation("Evento publicado: {Event} ({EventId})", TEvent.EventName, @event.EventId);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;

        var conn = await connection.GetAsync(ct);
        _channel = await conn.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        await _channel.ExchangeDeclareAsync(Topology.EventsExchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        return _channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        _lock.Dispose();
    }
}
