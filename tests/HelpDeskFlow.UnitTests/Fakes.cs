using HelpDeskFlow.Contracts;
using Identity.Application;
using Identity.Domain;
using Tickets.Application.Abstractions;
using Tickets.Domain.Entities;

namespace HelpDeskFlow.UnitTests;

/// <summary>Registro ordenado de "o que aconteceu", para provar a ORDEM de operações (ex.: evento antes do save).</summary>
public class Timeline
{
    public List<string> Entries { get; } = [];
}

public class RecordingPublisher(Timeline? timeline = null) : IEventPublisher
{
    public List<object> Events { get; } = [];

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : IIntegrationEvent
    {
        Events.Add(@event);
        timeline?.Entries.Add($"publish:{TEvent.EventName}");
        return Task.CompletedTask;
    }

    public IEnumerable<T> Of<T>() => Events.OfType<T>();
}

public class FakeUnitOfWork(Timeline? timeline = null) : IUnitOfWork
{
    public int Saves { get; private set; }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        Saves++;
        timeline?.Entries.Add("save");
        return Task.CompletedTask;
    }
}

public class FakeUserRepository : IUserRepository
{
    public List<User> Items { get; } = [];

    public Task<User?> GetByEmailAsync(string e, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(u => u.Email == e));
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(u => u.Id == id));
    public Task<bool> EmailExistsAsync(string e, CancellationToken ct) => Task.FromResult(Items.Any(u => u.Email == e));
    public Task<List<User>> ListByTenantAsync(Guid t, CancellationToken ct) => Task.FromResult(Items.Where(u => u.TenantId == t).ToList());
    public Task AddAsync(User user, CancellationToken ct) { Items.Add(user); return Task.CompletedTask; }
}

public class FakeTenantRepository : ITenantRepository
{
    public List<Tenant> Items { get; } = [];

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(t => t.Id == id));
    public Task<Tenant?> GetTrackedByIdAsync(Guid id, CancellationToken ct) => GetByIdAsync(id, ct);
    public Task AddAsync(Tenant tenant, CancellationToken ct) { Items.Add(tenant); return Task.CompletedTask; }
}

public class FakeRefreshTokenRepository : IRefreshTokenRepository
{
    public List<RefreshToken> Items { get; } = [];

    public Task<RefreshToken?> GetByHashAsync(string h, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(t => t.TokenHash == h));
    public Task AddAsync(RefreshToken token, CancellationToken ct) { Items.Add(token); return Task.CompletedTask; }

    public Task RevokeAllForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        foreach (var t in Items.Where(t => t.UserId == userId)) t.Revoke(nowUtc);
        return Task.CompletedTask;
    }
}

/// <summary>Hasher de mentira: rápido e determinístico, e permite contar quantas verificações foram simuladas.</summary>
public class FakeHasher : Identity.Application.IPasswordHasher
{
    public int SimulatedVerifications { get; private set; }

    public string Hash(string password) => "hash:" + password;
    public bool Verify(string hash, string password) => hash == "hash:" + password;
    public void SimulateVerification(string password) => SimulatedVerifications++;
}

public class FakeTokenService(TimeProvider clock) : ITokenService
{
    private int _counter;

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(7);

    public AccessToken CreateAccessToken(User user) =>
        new($"jwt-for-{user.Id}", clock.GetUtcNow().UtcDateTime.AddMinutes(15));

    public string GenerateRefreshToken() => $"refresh-{++_counter}";
    public string HashRefreshToken(string rawToken) => "h:" + rawToken;
}

// ───────────── Tickets ─────────────

public class FakeCurrentUser(Guid tenantId, Guid userId, bool isCustomer = false) : ICurrentUser
{
    public Guid TenantId { get; } = tenantId;
    public Guid UserId { get; } = userId;
    public bool IsCustomer { get; } = isCustomer;
}

public class FakeTicketRepository : ITicketRepository
{
    private readonly Timeline? _timeline;
    public FakeTicketRepository(Timeline? timeline = null) => _timeline = timeline;

    public List<Ticket> Items { get; } = [];
    public int Saves { get; private set; }

    public Task<Ticket?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(t => t.Id == id));
    public Task<List<Ticket>> ListAsync(CancellationToken ct) => Task.FromResult(Items.ToList());
    public Task AddAsync(Ticket ticket, CancellationToken ct) { Items.Add(ticket); return Task.CompletedTask; }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        Saves++;
        _timeline?.Entries.Add("save");
        return Task.CompletedTask;
    }

    public Task<List<Ticket>> SystemListUnattendedAsync(DateTime createdBeforeUtc, int take, CancellationToken ct) =>
        Task.FromResult(Items.Where(t => t.CreatedAt <= createdBeforeUtc).Take(take).ToList());

    public Task<Ticket?> SystemGetByIdAsync(Guid id, CancellationToken ct) => GetByIdAsync(id, ct);
}

public class FakeKnownUsers : IKnownUserRepository
{
    private readonly HashSet<(Guid Tenant, Guid User)> _staff = [];

    public void AddStaff(Guid tenantId, Guid userId) => _staff.Add((tenantId, userId));

    private readonly Dictionary<Guid, string> _names = [];

    public void AddName(Guid userId, string name) => _names[userId] = name;

    public Task<bool> IsStaffOfTenantAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        Task.FromResult(_staff.Contains((tenantId, userId)));

    public Task<Dictionary<Guid, string>> GetNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult(ids.Where(_names.ContainsKey).ToDictionary(id => id, id => _names[id]));

    public Task<List<StaffMember>> ListStaffAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(_staff.Where(s => s.Tenant == tenantId)
            .Select(s => new StaffMember(s.User, _names.GetValueOrDefault(s.User, "?"), "Agent")).ToList());
}
