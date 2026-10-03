using Microsoft.EntityFrameworkCore;
using Tickets.Application.Abstractions;

namespace Tickets.Infrastructure.Persistence;

public class KnownUserRepository(TicketsDbContext db) : IKnownUserRepository
{
    public Task<bool> IsStaffOfTenantAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        db.KnownUsers.AnyAsync(u => u.Id == userId && u.TenantId == tenantId
                                    && (u.Role == "Admin" || u.Role == "Agent"), ct);

    public Task<Dictionary<Guid, string>> GetNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        db.KnownUsers.AsNoTracking()
            .Where(u => u.TenantId == tenantId && ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

    public Task<List<StaffMember>> ListStaffAsync(Guid tenantId, CancellationToken ct) =>
        db.KnownUsers.AsNoTracking()
            .Where(u => u.TenantId == tenantId && (u.Role == "Admin" || u.Role == "Agent"))
            .OrderBy(u => u.Name)
            .Select(u => new StaffMember(u.Id, u.Name, u.Role))
            .ToListAsync(ct);
}
