namespace Tickets.Domain.Enums;

public enum TicketStatus
{
    Open = 0,
    InProgress = 1,
    Resolved = 2,
    Closed = 3
}

public enum TicketPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Urgent = 3
}
