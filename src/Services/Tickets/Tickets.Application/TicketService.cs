using HelpDeskFlow.Contracts;
using Tickets.Application.Abstractions;
using Tickets.Domain;
using Tickets.Domain.Entities;
using Tickets.Domain.Enums;

namespace Tickets.Application;

public record CreateTicketRequest(string Title, string Description, TicketPriority Priority);
public record AssignTicketRequest(Guid AssigneeId);

public record TicketResponse(
    Guid Id, Guid RequesterId, string Title, string Description, TicketStatus Status,
    TicketPriority Priority, Guid? AssigneeId, DateTime CreatedAt, DateTime? ClosedAt, DateTime? SlaBreachedAt,
    string? RequesterName = null, string? AssigneeName = null)
{
    public static TicketResponse From(Ticket t, IReadOnlyDictionary<Guid, string>? names = null) =>
        new(t.Id, t.RequesterId, t.Title, t.Description, t.Status, t.Priority, t.AssigneeId, t.CreatedAt, t.ClosedAt, t.SlaBreachedAt,
            names?.GetValueOrDefault(t.RequesterId),
            t.AssigneeId is { } a ? names?.GetValueOrDefault(a) : null);
}

public class TicketService(
    ITicketRepository repository,
    IKnownUserRepository knownUsers,
    ICurrentUser currentUser,
    IEventPublisher events)
{
    public async Task<TicketResponse> CreateAsync(CreateTicketRequest request, CancellationToken ct)
    {
        var ticket = Ticket.Open(currentUser.TenantId, currentUser.UserId, request.Title, request.Description, request.Priority);
        await repository.AddAsync(ticket, ct);

        // Outbox: o evento é registrado ANTES do SaveChanges e gravado na mesma transação do chamado.
        // Ou os dois existem, ou nenhum. O envio ao RabbitMQ acontece depois, em segundo plano.
        await events.PublishAsync(TicketCreated.Create(
            ticket.TenantId, ticket.Id, ticket.RequesterId, ticket.Title, ticket.Priority.ToString()));
        await repository.SaveChangesAsync(ct);

        return await ToResponseAsync(ticket, ct);
    }

    public async Task<List<TicketResponse>> ListAsync(CancellationToken ct)
    {
        var tickets = await repository.ListAsync(ct);
        var names = await NamesForAsync(tickets, ct);
        return tickets.Select(t => TicketResponse.From(t, names)).ToList();
    }

    public async Task<TicketResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var ticket = await repository.GetByIdAsync(id, ct);
        return ticket is null ? null : await ToResponseAsync(ticket, ct);
    }

    /// <summary>Equipe que pode assumir chamados da empresa atual (para a tela de atribuição).</summary>
    public Task<List<StaffMember>> ListStaffAsync(CancellationToken ct) =>
        knownUsers.ListStaffAsync(currentUser.TenantId, ct);

    public async Task<TicketResponse?> AssignAsync(Guid id, AssignTicketRequest request, CancellationToken ct)
    {
        // Valida o responsável usando a cópia local de usuários (alimentada por eventos do Identity).
        if (!await knownUsers.IsStaffOfTenantAsync(currentUser.TenantId, request.AssigneeId, ct))
            throw new DomainException("Responsável inválido: precisa ser Admin ou Agent da sua empresa.");

        return await ChangeAsync(id, t => t.AssignTo(request.AssigneeId),
            t => events.PublishAsync(TicketAssigned.Create(t.TenantId, t.Id, t.RequesterId, request.AssigneeId, t.Title)), ct);
    }

    public Task<TicketResponse?> ResolveAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Resolve(),
            t => events.PublishAsync(TicketResolved.Create(t.TenantId, t.Id, t.RequesterId, currentUser.UserId, t.Title)), ct);

    public Task<TicketResponse?> CloseAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Close(), null, ct);

    public Task<TicketResponse?> ReopenAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Reopen(), null, ct);

    private async Task<TicketResponse?> ChangeAsync(
        Guid id, Action<Ticket> change, Func<Ticket, Task>? stageEvent, CancellationToken ct)
    {
        var ticket = await repository.GetByIdAsync(id, ct);
        if (ticket is null) return null;

        change(ticket);
        if (stageEvent is not null)
            await stageEvent(ticket); // registra no Outbox, ainda sem salvar
        await repository.SaveChangesAsync(ct); // chamado + evento: uma única transação

        return await ToResponseAsync(ticket, ct);
    }

    private async Task<TicketResponse> ToResponseAsync(Ticket ticket, CancellationToken ct) =>
        TicketResponse.From(ticket, await NamesForAsync([ticket], ct));

    /// <summary>Resolve os nomes (solicitante e responsável) a partir da cópia local de usuários, numa única consulta.</summary>
    private async Task<Dictionary<Guid, string>> NamesForAsync(IEnumerable<Ticket> tickets, CancellationToken ct)
    {
        var ids = tickets
            .SelectMany(t => new[] { (Guid?)t.RequesterId, t.AssigneeId })
            .OfType<Guid>().Distinct().ToList();

        return ids.Count == 0 ? [] : await knownUsers.GetNamesAsync(currentUser.TenantId, ids, ct);
    }
}
