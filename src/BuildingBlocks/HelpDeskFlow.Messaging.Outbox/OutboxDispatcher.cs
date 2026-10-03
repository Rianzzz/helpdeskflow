using System.Text;
using HelpDeskFlow.Contracts;
using HelpDeskFlow.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HelpDeskFlow.Messaging.Outbox;

public static class OutboxExtensions
{
    /// <summary>Troca a publicação direta pelo Outbox transacional e liga o dispatcher em segundo plano.</summary>
    public static IServiceCollection AddOutbox<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.Replace(ServiceDescriptor.Scoped<IEventPublisher, OutboxEventPublisher<TContext>>());
        services.AddSingleton<IHostedService, OutboxDispatcher<TContext>>();
        return services;
    }
}

/// <summary>
/// Lê as mensagens pendentes do Outbox e as publica no RabbitMQ, em ordem. Garantia: "pelo menos uma vez".
/// Se cair depois de publicar e antes de marcar como enviada, a mensagem sai de novo: os consumidores são idempotentes.
/// </summary>
public sealed class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopeFactory,
    IRawEventPublisher publisher,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService where TContext : DbContext
{
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(3);

    private DateTime _lastCleanup = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                var dispatched = await DispatchBatchAsync(stoppingToken);
                if (dispatched == BatchSize) delay = TimeSpan.Zero; // ainda há fila: continua sem esperar
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Broker fora do ar, banco indisponível... as mensagens continuam seguras na tabela.
                logger.LogWarning(ex, "Falha ao despachar o Outbox; nova tentativa em {Seconds}s.", FailureBackoff.TotalSeconds);
                delay = FailureBackoff;
            }

            if (delay > TimeSpan.Zero) await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        // Nomes reais de tabela/colunas vêm do modelo do EF (nada de texto digitado por usuário).
        var entity = db.Model.FindEntityType(typeof(OutboxMessage))!;
        var table = entity.GetTableName()!;
        var schema = entity.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(table, schema);
        var processedAt = entity.FindProperty(nameof(OutboxMessage.ProcessedAt))!.GetColumnName(storeObject);
        var createdAt = entity.FindProperty(nameof(OutboxMessage.CreatedAt))!.GetColumnName(storeObject);
        var qualified = schema is null ? $"\"{table}\"" : $"\"{schema}\".\"{table}\"";

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // FOR UPDATE SKIP LOCKED: se houver várias instâncias do serviço, cada uma pega um lote diferente,
        // sem publicar a mesma mensagem duas vezes ao mesmo tempo.
#pragma warning disable EF1002
        var batch = await db.Set<OutboxMessage>()
            .FromSqlRaw($"SELECT * FROM {qualified} WHERE \"{processedAt}\" IS NULL ORDER BY \"{createdAt}\" LIMIT {BatchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);
#pragma warning restore EF1002

        var sent = 0;
        Exception? failure = null;
        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishRawAsync(message.Type, message.Id, message.OccurredAt,
                    Encoding.UTF8.GetBytes(message.Payload), ct);
                message.ProcessedAt = DateTime.UtcNow;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                failure = ex;
                break; // preserva a ordem: não pula para as próximas
            }
        }

        // Grava o que deu certo (e o contador de tentativas do que falhou) antes de reportar o erro.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (failure is not null) throw failure;
        return sent;
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastCleanup < TimeSpan.FromHours(1)) return;
        _lastCleanup = DateTime.UtcNow;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var cutoff = DateTime.UtcNow - Retention;
        var removed = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        if (removed > 0) logger.LogInformation("Outbox: {Count} mensagens antigas removidas.", removed);
    }
}
