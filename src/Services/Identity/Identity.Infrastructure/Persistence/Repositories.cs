using Identity.Application;
using Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Infrastructure.Persistence;

public class UserRepository(IdentityDbContext db) : IUserRepository
{
    public Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Email == normalizedEmail, ct);

    public Task<List<User>> ListByTenantAsync(Guid tenantId, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.TenantId == tenantId).OrderBy(u => u.Name).ToListAsync(ct);

    public async Task AddAsync(User user, CancellationToken ct) => await db.Users.AddAsync(user, ct);
}

public class TenantRepository(IdentityDbContext db) : ITenantRepository
{
    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<Tenant?> GetTrackedByIdAsync(Guid id, CancellationToken ct) =>
        db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task AddAsync(Tenant tenant, CancellationToken ct) => await db.Tenants.AddAsync(tenant, ct);
}

public class RefreshTokenRepository(IdentityDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);

    public async Task AddAsync(RefreshToken token, CancellationToken ct) => await db.RefreshTokens.AddAsync(token, ct);

    public Task RevokeAllForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct) =>
        db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, nowUtc), ct);
}
