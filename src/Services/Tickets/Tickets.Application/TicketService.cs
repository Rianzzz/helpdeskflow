using Tickets.Application.Abstractions;
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

public class TicketService(ITicketRepository repository, ICurrentUser currentUser)
{
    public async Task<TicketResponse> CreateAsync(CreateTicketRequest request, CancellationToken ct)
    {
        var ticket = Ticket.Open(currentUser.TenantId, currentUser.UserId, request.Title, request.Description, request.Priority);
        await repository.AddAsync(ticket, ct);
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

    public Task<TicketResponse?> AssignAsync(Guid id, AssignTicketRequest request, CancellationToken ct) =>
        ChangeAsync(id, t => t.AssignTo(request.AssigneeId), ct);

    public Task<TicketResponse?> ResolveAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Resolve(), ct);

    public Task<TicketResponse?> CloseAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Close(), ct);

    public Task<TicketResponse?> ReopenAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, t => t.Reopen(), ct);

    private async Task<TicketResponse?> ChangeAsync(Guid id, Action<Ticket> change, CancellationToken ct)
    {
        var ticket = await repository.GetByIdAsync(id, ct);
        if (ticket is null) return null;

        change(ticket);
        await repository.SaveChangesAsync(ct);
        return TicketResponse.From(ticket);
    }
}
