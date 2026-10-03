namespace Identity.Domain;

public enum UserRole { Admin = 0, Agent = 1, Customer = 2 }

public class DomainException(string message) : Exception(message);

/// <summary>Empresa cliente do SaaS.</summary>
public class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Tenant() { }

    public static Tenant Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 150)
            throw new DomainException("Nome da empresa inválido (obrigatório, até 150 caracteres).");

        return new Tenant { Id = Guid.NewGuid(), Name = name.Trim(), IsActive = true, CreatedAt = DateTime.UtcNow };
    }
}

public class User
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }
    public bool IsActive { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTime? LockoutEnd { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private User() { }

    public static User Create(Guid tenantId, string name, string email, string passwordHash, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 150)
            throw new DomainException("Nome inválido (obrigatório, até 150 caracteres).");

        return new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            Email = NormalizeEmail(email),
            PasswordHash = passwordHash,
            Role = role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public bool IsLockedOut(DateTime nowUtc) => LockoutEnd is { } end && end > nowUtc;

    /// <summary>
    /// Proteção contra força bruta: após 5 senhas erradas seguidas, a conta é bloqueada por 15 minutos.
    /// </summary>
    public void RegisterFailedLogin(DateTime nowUtc)
    {
        // Se o bloqueio anterior já expirou, recomeça a contagem.
        if (LockoutEnd is { } end && end <= nowUtc)
        {
            FailedLoginCount = 0;
            LockoutEnd = null;
        }

        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedAttempts)
            LockoutEnd = nowUtc.Add(LockoutDuration);
    }

    public void RegisterSuccessfulLogin()
    {
        FailedLoginCount = 0;
        LockoutEnd = null;
    }

    public void UpdatePasswordHash(string newHash) => PasswordHash = newHash;
}

/// <summary>
/// Refresh token. O valor "cru" é entregue ao cliente UMA vez; no banco guardamos só o hash,
/// então um vazamento do banco não entrega tokens utilizáveis.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Issue(Guid userId, string tokenHash, DateTime nowUtc, TimeSpan lifetime) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = tokenHash,
        CreatedAt = nowUtc,
        ExpiresAt = nowUtc.Add(lifetime)
    };

    public bool IsRevoked => RevokedAt is not null;
    public bool IsExpired(DateTime nowUtc) => ExpiresAt <= nowUtc;

    public void Revoke(DateTime nowUtc) => RevokedAt ??= nowUtc;
}
