using Tickets.Domain.Enums;

namespace Tickets.Domain.Entities;

/// <summary>
/// Chamado de suporte. Toda entidade de um SaaS multi-tenant carrega o TenantId:
/// é ele que garante que uma empresa nunca enxergue os dados de outra.
/// </summary>
public class Ticket
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RequesterId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public TicketStatus Status { get; private set; }
    public TicketPriority Priority { get; private set; }
    public Guid? AssigneeId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    /// <summary>Quando o prazo de primeiro atendimento (SLA) estourou. Nulo = ainda não estourou.</summary>
    public DateTime? SlaBreachedAt { get; private set; }

    // Exigido pelo EF Core
    private Ticket() { }

    public static Ticket Open(Guid tenantId, Guid requesterId, string title, string description, TicketPriority priority)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException("O título do chamado é obrigatório.");

        return new Ticket
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RequesterId = requesterId,
            Title = title.Trim(),
            Description = description?.Trim() ?? string.Empty,
            Priority = priority,
            Status = TicketStatus.Open,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void AssignTo(Guid assigneeId)
    {
        EnsureNotClosed();
        AssigneeId = assigneeId;
        if (Status == TicketStatus.Open)
            Status = TicketStatus.InProgress;
    }

    public void Resolve()
    {
        EnsureNotClosed();
        Status = TicketStatus.Resolved;
    }

    public void Close()
    {
        if (Status != TicketStatus.Resolved)
            throw new DomainException("Só é possível fechar um chamado que já foi resolvido.");

        Status = TicketStatus.Closed;
        ClosedAt = DateTime.UtcNow;
    }

    public void Reopen()
    {
        if (Status is not (TicketStatus.Resolved or TicketStatus.Closed))
            throw new DomainException("Só é possível reabrir chamados resolvidos ou fechados.");

        Status = TicketStatus.Open;
        ClosedAt = null;
    }

    /// <summary>
    /// SLA de primeiro atendimento: um chamado que continua ABERTO e SEM RESPONSÁVEL além do prazo
    /// da sua prioridade está "estourado". Só é considerado uma vez (SlaBreachedAt evita alertas repetidos).
    /// </summary>
    public bool IsSlaOverdue(DateTime nowUtc, TimeSpan limit) =>
        Status == TicketStatus.Open && AssigneeId is null && SlaBreachedAt is null && nowUtc - CreatedAt >= limit;

    public void MarkSlaBreached(DateTime nowUtc) => SlaBreachedAt = nowUtc;

    private void EnsureNotClosed()
    {
        if (Status == TicketStatus.Closed)
            throw new DomainException("Chamado fechado não pode ser alterado. Reabra-o primeiro.");
    }
}
