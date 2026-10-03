namespace Tickets.Domain.Entities;

/// <summary>
/// Um comentário na conversa de um chamado. Pode ser uma resposta pública (visível a quem abriu o chamado) ou uma
/// NOTA INTERNA (só a equipe vê): é onde o atendimento troca contexto, sem expor ao cliente.
/// </summary>
public class TicketComment
{
    public const int MaxBodyLength = 4000;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public bool IsInternal { get; private set; }
    public DateTime CreatedAt { get; private set; }

    // Exigido pelo EF Core
    private TicketComment() { }

    public static TicketComment Create(Guid tenantId, Guid ticketId, Guid authorId, string body, bool isInternal)
    {
        var text = body?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new DomainException("O comentário não pode ser vazio.");
        if (text.Length > MaxBodyLength)
            throw new DomainException($"O comentário pode ter no máximo {MaxBodyLength} caracteres.");

        return new TicketComment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TicketId = ticketId,
            AuthorId = authorId,
            Body = text,
            IsInternal = isInternal,
            CreatedAt = DateTime.UtcNow
        };
    }
}
