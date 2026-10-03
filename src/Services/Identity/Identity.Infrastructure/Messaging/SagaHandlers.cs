using HelpDeskFlow.Contracts;
using Identity.Domain;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Identity.Infrastructure.Messaging;

/// <summary>
/// FINAL FELIZ da saga: o serviço Tenants aprovou. Ativa a empresa e só agora anuncia o administrador
/// (UserRegistered) aos demais serviços. Empresas que falham nunca vazam usuários para outros serviços.
/// Idempotente: se a empresa já está ativa, não faz nada.
/// </summary>
public class TenantProvisionedHandler(IdentityDbContext db, IEventPublisher events, ILogger<TenantProvisionedHandler> logger)
    : IEventHandler<TenantProvisioned>
{
    public async Task HandleAsync(TenantProvisioned e, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == e.TenantId, ct);
        if (tenant is null || tenant.Status != TenantStatus.Provisioning)
        {
            // Já ativa (reentrega) ou já falhou/expirou (resposta tardia): nada a fazer.
            logger.LogInformation("TenantProvisioned ignorado para {TenantId} (status: {Status}).", e.TenantId, tenant?.Status);
            return;
        }

        var admin = await db.Users.Where(u => u.TenantId == tenant.Id && u.Role == UserRole.Admin)
            .OrderBy(u => u.CreatedAt).FirstAsync(ct);

        tenant.Activate(e.MaxUsers);
        await events.PublishAsync(UserRegistered.Create(tenant.Id, admin.Id, admin.Name, admin.Email, admin.Role.ToString()));
        await events.PublishAsync(TenantActivated.Create(tenant.Id, tenant.Name, admin.Id, admin.Name, admin.Email));
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Empresa {Name} ({TenantId}) ativada.", tenant.Name, tenant.Id);
    }
}

/// <summary>
/// COMPENSAÇÃO: o Tenants recusou. Desfazemos o que o passo 1 criou: a empresa fica "Failed" (com o motivo, para
/// consulta) e o administrador provisório é removido, liberando o e-mail para uma nova tentativa.
/// </summary>
public class TenantProvisioningFailedHandler(IdentityDbContext db, ILogger<TenantProvisioningFailedHandler> logger)
    : IEventHandler<TenantProvisioningFailed>
{
    public async Task HandleAsync(TenantProvisioningFailed e, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == e.TenantId, ct);
        if (tenant is null || tenant.Status != TenantStatus.Provisioning)
        {
            logger.LogInformation("TenantProvisioningFailed ignorado para {TenantId} (status: {Status}).", e.TenantId, tenant?.Status);
            return;
        }

        await TenantCompensation.FailAsync(db, tenant, e.Reason, ct);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Cadastro da empresa {Name} desfeito: {Reason}", tenant.Name, e.Reason);
    }
}

public static class TenantCompensation
{
    /// <summary>Marca a empresa como falha e remove os usuários provisórios (refresh tokens saem por cascata).</summary>
    public static async Task FailAsync(IdentityDbContext db, Tenant tenant, string reason, CancellationToken ct)
    {
        tenant.Fail(reason);
        var users = await db.Users.Where(u => u.TenantId == tenant.Id).ToListAsync(ct);
        db.Users.RemoveRange(users);
    }
}
