using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace HelpDeskFlow.Messaging.RabbitMq;

public class MessagingOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string VirtualHost { get; set; } = "/";

    /// <summary>Nome do serviço dono desta instância (ex.: "tickets"). Prefixa o nome das filas.</summary>
    public string ServiceName { get; set; } = string.Empty;
}

public static class Topology
{
    /// <summary>Exchange "topic": roteia cada evento pela routing key (ex.: "tickets.ticket-created").</summary>
    public const string EventsExchange = "helpdeskflow.events";

    /// <summary>Mensagens que falharam de vez vão para cá (dead-letter) e ficam guardadas para análise.</summary>
    public const string DeadLetterExchange = "helpdeskflow.dlx";
}

/// <summary>Uma conexão por processo (conexões são caras; canais são baratos). Reconecta sozinha se cair.</summary>
public sealed class RabbitConnection(IOptions<MessagingOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;

        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;

            var o = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.Host,
                Port = o.Port,
                UserName = o.User,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                ClientProvidedName = o.ServiceName,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
            };

            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _lock.Dispose();
    }
}
