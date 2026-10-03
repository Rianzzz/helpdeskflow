namespace Tenants.Api.Domain;

public enum TenantPlan { Free = 0, Pro = 1, Enterprise = 2 }

/// <summary>Perfil comercial da empresa: nome, plano e limites. Dono desses dados: o serviço Tenants.</summary>
public class TenantProfile
{
    public Guid Id { get; private set; }               // = TenantId do Identity
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty; // base da regra "nome único"
    public TenantPlan Plan { get; private set; }
    public int MaxUsers { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private TenantProfile() { }

    public static TenantProfile CreateFree(Guid id, string name) => new()
    {
        Id = id,
        Name = name.Trim(),
        NormalizedName = Normalize(name),
        Plan = TenantPlan.Free,
        MaxUsers = 5,
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>Minúsculas, sem espaços repetidos: "  ACME   Ltda " e "acme ltda" são a MESMA empresa.</summary>
    public static string Normalize(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
}

public enum OnboardingOutcome { Provisioned = 0, Rejected = 1, Expired = 2 }

/// <summary>
/// Estado da saga neste serviço: "o que decidimos sobre este cadastro". Serve a dois propósitos:
/// idempotência (reentrega do mesmo evento não decide de novo) e ordem (um "expirado" que chega ANTES do
/// "registrado" impede a criação de um perfil órfão).
/// </summary>
public class OnboardingState
{
    public Guid TenantId { get; private set; }
    public string CompanyName { get; private set; } = string.Empty;
    public OnboardingOutcome Outcome { get; private set; }
    public string? Reason { get; private set; }
    public DateTime DecidedAt { get; private set; }

    private OnboardingState() { }

    public static OnboardingState Decide(Guid tenantId, string companyName, OnboardingOutcome outcome, string? reason = null) => new()
    {
        TenantId = tenantId,
        CompanyName = companyName,
        Outcome = outcome,
        Reason = reason,
        DecidedAt = DateTime.UtcNow
    };

    public void Expire()
    {
        Outcome = OnboardingOutcome.Expired;
        Reason = "Prazo do onboarding esgotado.";
        DecidedAt = DateTime.UtcNow;
    }
}
