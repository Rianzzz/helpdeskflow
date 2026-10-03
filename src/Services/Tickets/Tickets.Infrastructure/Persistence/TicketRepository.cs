using Microsoft.EntityFrameworkCore;
using Tickets.Application.Abstractions;
using Tickets.Domain.Entities;
using Tickets.Domain.Enums;

namespace Tickets.Infrastructure.Persistence;

public class TicketRepository(TicketsDbContext db) : ITicketRepository
{
    public Task<Ticket?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<List<Ticket>> ListAsync(CancellationToken ct) =>
        db.Tickets.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(Ticket ticket, CancellationToken ct) =>
        await db.Tickets.AddAsync(ticket, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // IgnoreQueryFilters: usado só por jobs de sistema (sem usuário logado), que precisam ver todas as empresas.
    public Task<List<Ticket>> SystemListUnattendedAsync(DateTime createdBeforeUtc, int take, CancellationToken ct) =>
        db.Tickets.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Status == TicketStatus.Open && t.AssigneeId == null
                        && t.SlaBreachedAt == null && t.CreatedAt <= createdBeforeUtc)
            .OrderBy(t => t.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<Ticket?> SystemGetByIdAsync(Guid id, CancellationToken ct) =>
        db.Tickets.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == id, ct);
}
