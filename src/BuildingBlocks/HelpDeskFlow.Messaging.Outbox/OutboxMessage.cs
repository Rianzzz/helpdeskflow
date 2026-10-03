using Microsoft.EntityFrameworkCore;

namespace HelpDeskFlow.Messaging.Outbox;

/// <summary>
/// Evento aguardando publicação. Fica na MESMA tabela/transação do dado de negócio: ou os dois são gravados,
/// ou nenhum. Um processo em segundo plano (dispatcher) lê esta tabela e publica no RabbitMQ.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }                        // = EventId
    public string Type { get; set; } = string.Empty;    // routing key
    public string Payload { get; set; } = string.Empty; // JSON do evento
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Contexto de trace (W3C "traceparent") da requisição que gerou o evento. O dispatcher o restaura ao publicar,
    /// então o trace continua ligado mesmo o envio acontecendo depois, em outro momento.
    /// </summary>
    public string? TraceParent { get; set; }
}

public static class OutboxTelemetry
{
    /// <summary>Mesmo nome registrado em HelpDeskFlow.Observability (AddSource).</summary>
    public static readonly System.Diagnostics.ActivitySource Source = new("HelpDeskFlow");
}

public static class OutboxModelExtensions
{
    /// <summary>Chame no OnModelCreating do DbContext do serviço.</summary>
    public static ModelBuilder ApplyOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(o => o.Id);
            e.Property(o => o.Type).HasMaxLength(200).IsRequired();
            e.Property(o => o.Payload).IsRequired();
            e.Property(o => o.LastError).HasMaxLength(2000);
            e.Property(o => o.TraceParent).HasMaxLength(100);
            e.HasIndex(o => new { o.ProcessedAt, o.CreatedAt });
        });
        return modelBuilder;
    }
}
