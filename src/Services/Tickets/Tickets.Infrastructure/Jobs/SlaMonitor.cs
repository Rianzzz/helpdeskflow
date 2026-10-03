using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tickets.Application;

namespace Tickets.Infrastructure.Jobs;

/// <summary>
/// Job agendado: de tempos em tempos procura chamados que estouraram o prazo de primeiro atendimento
/// e dispara o evento TicketSlaBreached. Nenhuma requisição HTTP envolvida.
/// </summary>
public sealed class SlaMonitor(IServiceScopeFactory scopeFactory, SlaPolicy policy, ILogger<SlaMonitor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, policy.CheckIntervalSeconds));
        logger.LogInformation("Monitor de SLA ativo (verificação a cada {Interval}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha na verificação de SLA; tentaremos de novo no próximo ciclo.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        List<Guid> overdue;
        await using (var scope = scopeFactory.CreateAsyncScope())
            overdue = await scope.ServiceProvider.GetRequiredService<SlaService>().FindOverdueAsync(ct);

        foreach (var ticketId in overdue)
        {
            // Um escopo (e um DbContext) por chamado: se um falhar, não contamina os outros.
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                if (await scope.ServiceProvider.GetRequiredService<SlaService>().BreachAsync(ticketId, ct))
                    logger.LogWarning("SLA estourado: chamado {TicketId}.", ticketId);
            }
            catch (DbUpdateException ex)
            {
                // Outra instância do serviço registrou o mesmo alerta primeiro (mesma chave no Outbox): tudo certo.
                logger.LogInformation(ex, "Chamado {TicketId} já processado por outra instância.", ticketId);
            }
        }
    }
}
