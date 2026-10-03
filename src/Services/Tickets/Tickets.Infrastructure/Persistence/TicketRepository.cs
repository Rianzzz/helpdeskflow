using Microsoft.EntityFrameworkCore;
using Tickets.Application.Abstractions;
using Tickets.Domain.Entities;

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
}
