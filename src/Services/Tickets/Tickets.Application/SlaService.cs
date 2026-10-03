using HelpDeskFlow.Contracts;
using Tickets.Application.Abstractions;
using Tickets.Domain.Enums;

namespace Tickets.Application;

/// <summary>Prazos de primeiro atendimento por prioridade (em minutos). Configurável em appsettings ("Sla").</summary>
public class SlaPolicy
{
    public double UrgentMinutes { get; set; } = 15;
    public double HighMinutes { get; set; } = 60;
    public double MediumMinutes { get; set; } = 240;
    public double LowMinutes { get; set; } = 1440;

    /// <summary>De quanto em quanto tempo o job verifica os prazos.</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    public TimeSpan LimitFor(TicketPriority priority) => TimeSpan.FromMinutes(priority switch
    {
        TicketPriority.Urgent => UrgentMinutes,
        TicketPriority.High => HighMinutes,
        TicketPriority.Medium => MediumMinutes,
        _ => LowMinutes
    });

    public TimeSpan ShortestLimit => TimeSpan.FromMinutes(
        Math.Min(Math.Min(UrgentMinutes, HighMinutes), Math.Min(MediumMinutes, LowMinutes)));
}

/// <summary>
/// Operações de SLA. Diferente do resto do serviço, estas rodam em segundo plano e enxergam TODAS as empresas
/// (não há usuário logado). Por isso usam métodos de repositório explicitamente "do sistema", que ignoram o
/// filtro de tenant; nenhum endpoint HTTP chama este código.
/// </summary>
public class SlaService(ITicketRepository repository, IEventPublisher events, SlaPolicy policy, TimeProvider clock)
{
    private const int BatchSize = 100;

    public async Task<List<Guid>> FindOverdueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var candidates = await repository.SystemListUnattendedAsync(now - policy.ShortestLimit, BatchSize, ct);

        return candidates
            .Where(t => t.IsSlaOverdue(now, policy.LimitFor(t.Priority)))
            .Select(t => t.Id)
            .ToList();
    }

    /// <summary>Marca o estouro e registra o evento no Outbox, na mesma transação. Idempotente.</summary>
    public async Task<bool> BreachAsync(Guid ticketId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ticket = await repository.SystemGetByIdAsync(ticketId, ct);

        // Reconfere com o dado atual: o chamado pode ter sido atribuído desde a listagem.
        if (ticket is null || !ticket.IsSlaOverdue(now, policy.LimitFor(ticket.Priority)))
            return false;

        ticket.MarkSlaBreached(now);
        await events.PublishAsync(TicketSlaBreached.Create(
            ticket.TenantId, ticket.Id, ticket.RequesterId, ticket.Title, ticket.Priority.ToString(),
            (int)(now - ticket.CreatedAt).TotalMinutes));
        await repository.SaveChangesAsync(ct);
        return true;
    }
}
