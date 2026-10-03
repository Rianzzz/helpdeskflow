using Identity.Application;
using HelpDeskFlow.Messaging.Outbox;
using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Identity.Infrastructure.Persistence;

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("identity");
        b.ApplyOutbox();

        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(150).IsRequired();
            e.Property(t => t.FailureReason).HasMaxLength(500);
            e.Ignore(t => t.IsActive);
            // O status também é o "token de concorrência": se a saga (Activate) e o timeout (Fail) agirem ao mesmo
            // tempo sobre a mesma empresa, o segundo a salvar recebe um erro de concorrência em vez de sobrescrever.
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(20).IsConcurrencyToken();
            // O contador de vagas também protege contra corrida: dois cadastros de usuário simultâneos não furam o limite.
            e.Property(t => t.UserCount).IsConcurrencyToken();
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Name).HasMaxLength(150).IsRequired();
            e.Property(u => u.Email).HasMaxLength(254).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            // E-mail único no sistema inteiro: o login não pede "qual empresa", só e-mail e senha.
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.TenantId);
            e.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(r => r.Id);
            e.Property(r => r.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(r => r.TokenHash).IsUnique();
            e.HasIndex(r => r.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await base.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Dois cadastros simultâneos com o mesmo e-mail: o índice único do banco é a palavra final.
            throw new ConflictException("E-mail já cadastrado.");
        }
        catch (DbUpdateConcurrencyException)
        {
            // Outra requisição alterou a MESMA empresa no mesmo instante (ex.: duas pessoas adicionando o último usuário
            // do plano). Quem chegou depois recebe um erro claro, em vez de os dois passarem e estourarem o limite.
            throw new ConflictException("Outra alteração aconteceu ao mesmo tempo. Tente novamente.");
        }
    }
}
