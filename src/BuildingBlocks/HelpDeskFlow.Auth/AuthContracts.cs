namespace HelpDeskFlow.Auth;

/// <summary>Papéis do sistema. É o "contrato" entre o Identity (que emite) e os demais serviços (que validam).</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Agent = "Agent";
    public const string Customer = "Customer";
}

/// <summary>Nomes das claims dentro do JWT.</summary>
public static class AppClaims
{
    public const string Subject = "sub";       // id do usuário
    public const string TenantId = "tenant_id"; // empresa do usuário
    public const string Role = "role";
    public const string Email = "email";
}

public static class Policies
{
    /// <summary>Admin ou Agent (equipe de atendimento, não clientes).</summary>
    public const string Staff = "Staff";
    public const string AdminOnly = "AdminOnly";
}

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "helpdeskflow-identity";
    public string Audience { get; set; } = "helpdeskflow-api";

    /// <summary>
    /// Chave de assinatura (HS256). NUNCA vai para o Git em produção: use variável de ambiente
    /// ou um cofre de segredos. Mínimo de 32 caracteres.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}
