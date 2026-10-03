using System.Text.Json;
using HelpDeskFlow.Contracts;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace HelpDeskFlow.Messaging.RabbitMq;

public static class MessageSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Publica uma mensagem já serializada. É o que o dispatcher do Outbox usa.</summary>
public interface IRawEventPublisher
{
    Task PublishRawAsync(string routingKey, Guid messageId, DateTime occurredAt, ReadOnlyMemory<byte> body, CancellationToken ct);
}

public sealed class RabbitEventPublisher(RabbitConnection connection, ILogger<RabbitEventPublisher> logger)
    : IEventPublisher, IRawEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChannel? _channel;

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent =>
        PublishRawAsync(TEvent.EventName, @event.EventId, @event.OccurredAt,
            JsonSerializer.SerializeToUtf8Bytes(@event, MessageSerializer.Options), ct);

    public async Task PublishRawAsync(
        string routingKey, Guid messageId, DateTime occurredAt, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var props = new BasicProperties
        {
            MessageId = messageId.ToString(),
            Type = routingKey,
            ContentType = "application/json",
            Persistent = true, // a mensagem sobrevive a um restart do RabbitMQ
            Timestamp = new AmqpTimestamp(
                new DateTimeOffset(DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc)).ToUnixTimeSeconds())
        };

        await _lock.WaitAsync(ct);
        try
        {
            var channel = await GetChannelAsync(ct);

            // Com "publisher confirms", este await só termina quando o broker CONFIRMA que recebeu e gravou
            // a mensagem. Sem isso, "publicou" não significaria "o RabbitMQ realmente tem a mensagem".
            await channel.BasicPublishAsync(Topology.EventsExchange, routingKey, mandatory: false, props, body, ct);
            logger.LogInformation("Evento publicado: {Event} ({EventId})", routingKey, messageId);
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
        try
        {
            if (_channel is not null) await _channel.DisposeAsync();
        }
        catch (Exception)
        {
            // Mesmo raciocínio da conexão: erro ao fechar o canal no desligamento não é acionável.
        }

        _lock.Dispose();
    }
}
