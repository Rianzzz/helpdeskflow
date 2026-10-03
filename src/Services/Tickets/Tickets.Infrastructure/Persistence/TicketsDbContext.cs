using Microsoft.EntityFrameworkCore;
using Tickets.Application.Abstractions;
using Tickets.Domain.Entities;

namespace Tickets.Infrastructure.Persistence;

public class TicketsDbContext(DbContextOptions<TicketsDbContext> options, ITenantProvider tenant)
    : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();

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
            // "WHERE tenant_id = <tenant atual>". Isolamento entre empresas sem depender
            // de cada desenvolvedor lembrar de filtrar manualmente.
            e.HasQueryFilter(t => t.TenantId == tenant.TenantId);
        });
    }
}
