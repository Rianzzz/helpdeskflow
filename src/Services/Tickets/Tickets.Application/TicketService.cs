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
    TicketPriority Priority, Guid? AssigneeId, DateTime CreatedAt, DateTime? ClosedAt)
{
    public static TicketResponse From(Ticket t) =>
        new(t.Id, t.RequesterId, t.Title, t.Description, t.Status, t.Priority, t.AssigneeId, t.CreatedAt, t.ClosedAt);
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

        return TicketResponse.From(ticket);
    }

    public async Task<List<TicketResponse>> ListAsync(CancellationToken ct) =>
        (await repository.ListAsync(ct)).Select(TicketResponse.From).ToList();

    public async Task<TicketResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var ticket = await repository.GetByIdAsync(id, ct);
        return ticket is null ? null : TicketResponse.From(ticket);
    }

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

        return TicketResponse.From(ticket);
    }
}
