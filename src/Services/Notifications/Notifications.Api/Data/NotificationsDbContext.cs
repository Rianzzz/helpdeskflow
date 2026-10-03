using Microsoft.EntityFrameworkCore;
using Notifications.Api.Domain;

namespace Notifications.Api.Data;

public class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<KnownUser> KnownUsers => Set<KnownUser>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("notifications");

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(n => n.Id);
            e.Property(n => n.RecipientEmail).HasMaxLength(254).IsRequired();
            e.Property(n => n.Subject).HasMaxLength(200).IsRequired();
            e.Property(n => n.Body).HasMaxLength(2000).IsRequired();
            e.HasIndex(n => new { n.TenantId, n.RecipientUserId, n.CreatedAt });
        });

        b.Entity<KnownUser>(e =>
        {
            e.ToTable("known_users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Name).HasMaxLength(150).IsRequired();
            e.Property(u => u.Email).HasMaxLength(254).IsRequired();
            e.Property(u => u.Role).HasMaxLength(20).IsRequired();
            e.HasIndex(u => u.TenantId);
        });

        b.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("processed_messages");
            e.HasKey(p => p.Id);
            e.Property(p => p.Handler).HasMaxLength(100).IsRequired();
            e.HasIndex(p => new { p.EventId, p.Handler }).IsUnique();
        });
    }
}
