using Microsoft.EntityFrameworkCore;
using Tickets.Application.Abstractions;
using Tickets.Domain.Entities;

namespace Tickets.Infrastructure.Persistence;

public class TicketsDbContext(DbContextOptions<TicketsDbContext> options, ICurrentUser currentUser)
    : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<KnownUser> KnownUsers => Set<KnownUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("tickets");

        modelBuilder.Entity<Ticket>(e =>
        {
            e.ToTable("tickets");
            e.HasKey(t => t.Id);
            e.Property(t => t.Title).HasMaxLength(200).IsRequired();
            e.Property(t => t.Description).HasMaxLength(4000).IsRequired();
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(t => new { t.TenantId, t.Status });

            // Global Query Filter: TODA consulta em Tickets recebe automaticamente
            //  1) "WHERE tenant_id = <empresa atual>"  -> isolamento entre empresas
            //  2) clientes só veem os chamados que eles mesmos abriram -> isolamento entre usuários
            // Sem depender de cada desenvolvedor lembrar de filtrar manualmente.
            e.HasQueryFilter(t => t.TenantId == currentUser.TenantId
                                  && (!currentUser.IsCustomer || t.RequesterId == currentUser.UserId));
            e.HasIndex(t => t.RequesterId);
        });

        modelBuilder.Entity<KnownUser>(e =>
        {
            e.ToTable("known_users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Name).HasMaxLength(150).IsRequired();
            e.Property(u => u.Role).HasMaxLength(20).IsRequired();
            e.HasIndex(u => u.TenantId);
        });
    }
}
