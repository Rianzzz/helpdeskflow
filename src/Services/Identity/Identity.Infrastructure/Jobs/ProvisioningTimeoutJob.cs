using HelpDeskFlow.Contracts;
using Identity.Domain;
using Identity.Infrastructure.Messaging;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Identity.Infrastructure.Jobs;

public class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    /// <summary>Quanto tempo a saga pode ficar "em provisionamento" antes de o Identity desistir.</summary>
    public double TimeoutMinutes { get; set; } = 10;
    public int CheckIntervalSeconds { get; set; } = 30;
}

/// <summary>
/// TIMEOUT da saga. Uma saga sem prazo pode ficar presa para sempre (ex.: o serviço Tenants fora do ar).
/// Este job desiste de cadastros parados, compensa (como numa recusa) e avisa o Tenants para desfazer
/// qualquer perfil que ele tenha criado tarde demais.
/// </summary>
public sealed class ProvisioningTimeoutJob(
    IServiceScopeFactory scopeFactory, ProvisioningOptions options, TimeProvider clock, ILogger<ProvisioningTimeoutJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.CheckIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireStaleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao verificar cadastros expirados.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ExpireStaleAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(options.TimeoutMinutes);

        List<Guid> staleIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            staleIds = await db.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Provisioning && t.CreatedAt <= cutoff)
                .OrderBy(t => t.CreatedAt).Select(t => t.Id).Take(50).ToListAsync(ct);
        }

        foreach (var id in staleIds)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var events = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            try
            {
                var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (tenant is null || tenant.Status != TenantStatus.Provisioning) continue;

                await TenantCompensation.FailAsync(db, tenant, "O provisionamento não foi concluído a tempo.", ct);
                await events.PublishAsync(TenantRegistrationExpired.Create(tenant.Id));
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Cadastro da empresa {Name} ({TenantId}) expirou e foi desfeito.", tenant.Name, id);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A saga concluiu (ou falhou) no mesmo instante: o status mudou sob nossos pés. Quem chegou antes vence.
                logger.LogInformation("Empresa {TenantId} mudou de status durante a expiração; ignorando.", id);
            }
        }
    }
}
