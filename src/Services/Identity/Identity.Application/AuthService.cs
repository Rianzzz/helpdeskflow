using System.Net.Mail;
using HelpDeskFlow.Contracts;
using Identity.Domain;

namespace Identity.Application;

public record RegisterTenantRequest(string CompanyName, string AdminName, string Email, string Password);
public record RegisterTenantResponse(Guid TenantId, string Status);
public record TenantStatusResponse(Guid TenantId, string Status, string? FailureReason);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record CreateUserRequest(string Name, string Email, string Password, UserRole Role);

public record UserResponse(Guid Id, Guid TenantId, string Name, string Email, UserRole Role, bool IsActive)
{
    public static UserResponse From(User u) => new(u.Id, u.TenantId, u.Name, u.Email, u.Role, u.IsActive);
}

public record AuthResponse(
    string AccessToken, string TokenType, int ExpiresInSeconds, string RefreshToken, UserResponse User);

public class AuthService(
    IUserRepository users,
    ITenantRepository tenants,
    IRefreshTokenRepository refreshTokens,
    IUnitOfWork unitOfWork,
    IPasswordHasher hasher,
    ITokenService tokens,
    TimeProvider clock,
    IEventPublisher events)
{
    /// <summary>
    /// PASSO 1 da saga de onboarding. Cria a empresa EM PROVISIONAMENTO e o administrador, e avisa o serviço Tenants
    /// (evento TenantRegistered, pelo Outbox). Não emite tokens: a empresa só pode ser usada quando a saga concluir.
    /// O evento NÃO carrega a senha. Credenciais nunca viajam pelo broker.
    /// </summary>
    public async Task<RegisterTenantResponse> RegisterTenantAsync(RegisterTenantRequest request, CancellationToken ct)
    {
        var email = ValidateEmail(request.Email);
        PasswordPolicy.Validate(request.Password);

        if (await users.EmailExistsAsync(email, ct))
            throw new ConflictException("E-mail já cadastrado.");

        var tenant = Tenant.Create(request.CompanyName);
        var admin = User.Create(tenant.Id, request.AdminName, email, hasher.Hash(request.Password), UserRole.Admin);

        await tenants.AddAsync(tenant, ct);
        await users.AddAsync(admin, ct);
        await events.PublishAsync(TenantRegistered.Create(tenant.Id, tenant.Name, admin.Id, admin.Name, admin.Email));
        await unitOfWork.SaveChangesAsync(ct); // empresa + admin + evento: tudo ou nada

        return new RegisterTenantResponse(tenant.Id, tenant.Status.ToString());
    }

    public async Task<TenantStatusResponse?> GetTenantStatusAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenants.GetByIdAsync(tenantId, ct);
        return tenant is null ? null : new TenantStatusResponse(tenant.Id, tenant.Status.ToString(), tenant.FailureReason);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        var user = await users.GetByEmailAsync(email, ct);

        if (user is null)
        {
            hasher.SimulateVerification(request.Password ?? string.Empty);
            throw new AuthenticationFailedException();
        }

        // Conta bloqueada: nem verifica a senha (e a resposta é idêntica à de senha errada).
        if (user.IsLockedOut(now))
            throw new AuthenticationFailedException();

        if (!hasher.Verify(user.PasswordHash, request.Password ?? string.Empty))
        {
            user.RegisterFailedLogin(now);
            await unitOfWork.SaveChangesAsync(ct);
            throw new AuthenticationFailedException();
        }

        await EnsureAccountIsUsableAsync(user, ct);

        user.RegisterSuccessfulLogin();
        var response = await IssueTokensAsync(user);
        await unitOfWork.SaveChangesAsync(ct);
        return response;
    }

    /// <summary>
    /// Troca um refresh token por um novo par de tokens (rotação): o token usado é invalidado.
    /// Se alguém apresentar um token JÁ usado, assumimos roubo e derrubamos todas as sessões do usuário.
    /// </summary>
    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            throw new AuthenticationFailedException();

        var stored = await refreshTokens.GetByHashAsync(tokens.HashRefreshToken(request.RefreshToken), ct)
                     ?? throw new AuthenticationFailedException();

        if (stored.IsRevoked)
        {
            await refreshTokens.RevokeAllForUserAsync(stored.UserId, now, ct);
            throw new AuthenticationFailedException();
        }

        if (stored.IsExpired(now))
            throw new AuthenticationFailedException();

        var user = await users.GetByIdAsync(stored.UserId, ct) ?? throw new AuthenticationFailedException();
        await EnsureAccountIsUsableAsync(user, ct);

        stored.Revoke(now);
        var response = await IssueTokensAsync(user);
        await unitOfWork.SaveChangesAsync(ct);
        return response;
    }

    public async Task LogoutAsync(RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) return;

        var stored = await refreshTokens.GetByHashAsync(tokens.HashRefreshToken(request.RefreshToken), ct);
        if (stored is null) return; // idempotente: não revela se o token existia

        stored.Revoke(clock.GetUtcNow().UtcDateTime);
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<UserResponse> CreateUserAsync(Guid tenantId, CreateUserRequest request, CancellationToken ct)
    {
        var email = ValidateEmail(request.Email);
        PasswordPolicy.Validate(request.Password);

        if (!Enum.IsDefined(request.Role))
            throw new DomainException("Papel inválido.");

        if (await users.EmailExistsAsync(email, ct))
            throw new ConflictException("E-mail já cadastrado.");

        var user = User.Create(tenantId, request.Name, email, hasher.Hash(request.Password), request.Role);
        await users.AddAsync(user, ct);
        await StageUserRegisteredAsync(user);
        await unitOfWork.SaveChangesAsync(ct); // usuário + evento na mesma transação (Outbox)
        return UserResponse.From(user);
    }

    public async Task<List<UserResponse>> ListUsersAsync(Guid tenantId, CancellationToken ct) =>
        (await users.ListByTenantAsync(tenantId, ct)).Select(UserResponse.From).ToList();

    public async Task<UserResponse?> GetUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(userId, ct);
        return user is null ? null : UserResponse.From(user);
    }

    /// <summary>
    /// Registra o evento no Outbox (ainda não é enviado ao RabbitMQ): ele só passa a existir quando o SaveChanges
    /// confirma a transação, junto com o usuário. Quem publica de verdade é o dispatcher, em segundo plano.
    /// </summary>
    private Task StageUserRegisteredAsync(User user) =>
        events.PublishAsync(UserRegistered.Create(
            user.TenantId, user.Id, user.Name, user.Email, user.Role.ToString()));

    private async Task EnsureAccountIsUsableAsync(User user, CancellationToken ct)
    {
        var tenant = await tenants.GetByIdAsync(user.TenantId, ct);
        if (!user.IsActive || tenant is null || !tenant.IsActive)
            throw new AuthenticationFailedException();
    }

    private async Task<AuthResponse> IssueTokensAsync(User user)
    {
        var access = tokens.CreateAccessToken(user);
        var rawRefresh = tokens.GenerateRefreshToken();
        var now = clock.GetUtcNow().UtcDateTime;

        await refreshTokens.AddAsync(
            RefreshToken.Issue(user.Id, tokens.HashRefreshToken(rawRefresh), now, tokens.RefreshTokenLifetime), default);

        var expiresIn = (int)(access.ExpiresAt - now).TotalSeconds;
        return new AuthResponse(access.Value, "Bearer", expiresIn, rawRefresh, UserResponse.From(user));
    }

    private static string ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 ||
            !MailAddress.TryCreate(email.Trim(), out var parsed) || parsed.Address != email.Trim())
            throw new DomainException("E-mail inválido.");

        return User.NormalizeEmail(email);
    }
}

public static class PasswordPolicy
{
    // Recomendação atual (NIST): priorizar comprimento em vez de regras de símbolos estranhos.
    public static void Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 10)
            throw new DomainException("A senha deve ter pelo menos 10 caracteres.");
        if (password.Length > 128)
            throw new DomainException("A senha deve ter no máximo 128 caracteres.");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new DomainException("A senha deve conter letras e números.");
    }
}
