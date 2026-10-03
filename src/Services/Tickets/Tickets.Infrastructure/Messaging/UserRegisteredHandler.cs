using HelpDeskFlow.Contracts;
using Tickets.Domain.Entities;
using Tickets.Infrastructure.Persistence;

namespace Tickets.Infrastructure.Messaging;

/// <summary>Mantém a cópia local de usuários. É idempotente por natureza: processar duas vezes dá o mesmo resultado.</summary>
public class UserRegisteredHandler(TicketsDbContext db) : IEventHandler<UserRegistered>
{
    public async Task HandleAsync(UserRegistered e, CancellationToken ct)
    {
        var existing = await db.KnownUsers.FindAsync([e.UserId], ct);
        if (existing is null)
            db.KnownUsers.Add(KnownUser.Create(e.UserId, e.TenantId, e.Name, e.Role));
        else
            existing.Update(e.Name, e.Role);

        await db.SaveChangesAsync(ct);
    }
}
