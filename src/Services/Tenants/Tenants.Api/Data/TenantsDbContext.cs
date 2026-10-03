using HelpDeskFlow.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Tenants.Api.Domain;

namespace Tenants.Api.Data;

public class TenantsDbContext(DbContextOptions<TenantsDbContext> options) : DbContext(options)
{
    public DbSet<TenantProfile> Profiles => Set<TenantProfile>();
    public DbSet<OnboardingState> OnboardingStates => Set<OnboardingState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("tenants");
        b.ApplyOutbox();

        b.Entity<TenantProfile>(e =>
        {
            e.ToTable("tenant_profiles");
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(150).IsRequired();
            e.Property(t => t.NormalizedName).HasMaxLength(150).IsRequired();
            e.Property(t => t.Plan).HasConversion<string>().HasMaxLength(20);
            // O banco é a palavra final sobre "nome único", mesmo com dois cadastros simultâneos.
            e.HasIndex(t => t.NormalizedName).IsUnique();
        });

        b.Entity<OnboardingState>(e =>
        {
            e.ToTable("onboarding_states");
            e.HasKey(s => s.TenantId);
            e.Property(s => s.CompanyName).HasMaxLength(150).IsRequired();
            e.Property(s => s.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.Reason).HasMaxLength(500);
        });
    }
}
