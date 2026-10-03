using Microsoft.EntityFrameworkCore;
using Tickets.Application.Abstractions;

namespace Tickets.Infrastructure.Persistence;

public class KnownUserRepository(TicketsDbContext db) : IKnownUserRepository
{
    public Task<bool> IsStaffOfTenantAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        db.KnownUsers.AnyAsync(u => u.Id == userId && u.TenantId == tenantId
                                    && (u.Role == "Admin" || u.Role == "Agent"), ct);
}
