namespace Tickets.Domain;

/// <summary>Violação de uma regra de negócio. A API converte isso em HTTP 400.</summary>
public class DomainException(string message) : Exception(message);
