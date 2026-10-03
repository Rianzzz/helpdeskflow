using HelpDeskFlow.Contracts;
using Microsoft.EntityFrameworkCore;
using Tenants.Api.Data;
using Tenants.Api.Domain;

namespace Tenants.Api.Messaging;

/// <summary>
/// Passo do meio da saga: decide se o cadastro da empresa pode virar um tenant de verdade.
/// Aprovar ou recusar são DECISÕES DE NEGÓCIO (publicam TenantProvisioned ou TenantProvisioningFailed),
/// não erros técnicos. Estado + evento são gravados juntos (Outbox), então a decisão nunca se perde.
/// </summary>
public class TenantRegisteredHandler(TenantsDbContext db, IEventPublisher events, ILogger<TenantRegisteredHandler> logger)
    : IEventHandler<TenantRegistered>
{
    private static readonly HashSet<string> ReservedNames =
        ["admin", "root", "system", "helpdeskflow", "support", "suporte"];

    public async Task HandleAsync(TenantRegistered e, CancellationToken ct)
    {
        // Idempotência (e ordem): se já decidimos sobre este cadastro, não decidimos de novo.
        if (await db.OnboardingStates.AnyAsync(s => s.TenantId == e.TenantId, ct))
        {
            logger.LogInformation("Cadastro {TenantId} já decidido; ignorando.", e.TenantId);
            return;
        }

        var normalized = TenantProfile.Normalize(e.CompanyName);
        string? rejection = null;

        if (ReservedNames.Contains(normalized))
            rejection = "Este nome de empresa é reservado.";
        else if (await db.Profiles.AnyAsync(p => p.NormalizedName == normalized, ct))
            rejection = "Já existe uma empresa com este nome.";

        if (rejection is null)
        {
            var profile = TenantProfile.CreateFree(e.TenantId, e.CompanyName);
            db.Profiles.Add(profile);
            db.OnboardingStates.Add(OnboardingState.Decide(e.TenantId, e.CompanyName, OnboardingOutcome.Provisioned));
            await events.PublishAsync(TenantProvisioned.Create(e.TenantId, profile.Plan.ToString()));
            logger.LogInformation("Empresa {Name} ({TenantId}) provisionada no plano {Plan}.", profile.Name, e.TenantId, profile.Plan);
        }
        else
        {
            db.OnboardingStates.Add(OnboardingState.Decide(e.TenantId, e.CompanyName, OnboardingOutcome.Rejected, rejection));
            await events.PublishAsync(TenantProvisioningFailed.Create(
                e.TenantId, e.CompanyName, e.AdminUserId, e.AdminName, e.AdminEmail, rejection));
            logger.LogWarning("Cadastro da empresa {Name} recusado: {Reason}", e.CompanyName, rejection);
        }

        // Estado + perfil + evento: uma única transação. Se dois processos decidirem ao mesmo tempo, a chave
        // primária do estado faz um deles falhar; o consumidor tenta de novo e cai no "já decidido" acima.
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// "Compensação da compensação": se o Identity desistiu da saga (timeout), qualquer perfil que já tenhamos criado
/// é desfeito, liberando o nome. Se o aviso chegar ANTES do cadastro, registramos "expirado" para que o
/// cadastro, ao chegar depois, seja ignorado em vez de criar um perfil órfão.
/// </summary>
public class TenantRegistrationExpiredHandler(TenantsDbContext db) : IEventHandler<TenantRegistrationExpired>
{
    public async Task HandleAsync(TenantRegistrationExpired e, CancellationToken ct)
    {
        var state = await db.OnboardingStates.FirstOrDefaultAsync(s => s.TenantId == e.TenantId, ct);

        if (state is null)
        {
            db.OnboardingStates.Add(OnboardingState.Decide(e.TenantId, "(desconhecida)", OnboardingOutcome.Expired,
                "Prazo do onboarding esgotado antes da decisão."));
        }
        else if (state.Outcome == OnboardingOutcome.Provisioned)
        {
            var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == e.TenantId, ct);
            if (profile is not null) db.Profiles.Remove(profile);
            state.Expire();
        }
        else
        {
            return; // já recusado ou expirado: nada a desfazer
        }

        await db.SaveChangesAsync(ct);
    }
}
