using Identity.Domain;

namespace Identity.Application;

// A camada Application define O QUE precisa existir; a Infrastructure decide COMO.

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct);
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct);
    Task<List<User>> ListByTenantAsync(Guid tenantId, CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
}

public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(Tenant tenant, CancellationToken ct);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);
    Task AddAsync(RefreshToken token, CancellationToken ct);

    /// <summary>Revoga TODOS os tokens ativos do usuário, imediatamente (fora da unidade de trabalho).</summary>
    Task RevokeAllForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct);
}

public interface IUnitOfWork
{
    /// <exception cref="ConflictException">Violação de unicidade (ex.: e-mail duplicado).</exception>
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);

    /// <summary>
    /// Gasta o mesmo tempo de uma verificação real. Usado quando o e-mail não existe, para que o tempo
    /// de resposta não revele se a conta existe (ataque de "timing").
    /// </summary>
    void SimulateVerification(string password);
}

public record AccessToken(string Value, DateTime ExpiresAt);

public interface ITokenService
{
    TimeSpan RefreshTokenLifetime { get; }
    AccessToken CreateAccessToken(User user);
    string GenerateRefreshToken();
    string HashRefreshToken(string rawToken);
}

public class ConflictException(string message) : Exception(message);

/// <summary>Credenciais inválidas, conta bloqueada, token expirado... A mensagem é sempre genérica de propósito.</summary>
public class AuthenticationFailedException()
    : Exception("Credenciais inválidas.");
