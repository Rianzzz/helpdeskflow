using HelpDeskFlow.Contracts;
using Identity.Application;
using Identity.Domain;
using Tenants.Api.Domain;
using Tickets.Application;
using Tickets.Domain.Entities;
using Tickets.Domain.Enums;
using TicketsDomainException = Tickets.Domain.DomainException;
using IdentityDomainException = Identity.Domain.DomainException;

namespace HelpDeskFlow.UnitTests;

public class TicketTests
{
    private static Ticket NewTicket(TicketPriority priority = TicketPriority.Medium) =>
        Ticket.Open(Guid.NewGuid(), Guid.NewGuid(), "Impressora", "sala 3", priority);

    [Fact]
    public void Open_starts_as_Open_without_assignee()
    {
        var t = NewTicket();

        Assert.Equal(TicketStatus.Open, t.Status);
        Assert.Null(t.AssigneeId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Open_requires_a_title(string title) =>
        Assert.Throws<TicketsDomainException>(() => Ticket.Open(Guid.NewGuid(), Guid.NewGuid(), title, "", TicketPriority.Low));

    [Fact]
    public void Assigning_moves_open_ticket_to_InProgress()
    {
        var t = NewTicket();
        var agent = Guid.NewGuid();

        t.AssignTo(agent);

        Assert.Equal(TicketStatus.InProgress, t.Status);
        Assert.Equal(agent, t.AssigneeId);
    }

    [Fact]
    public void Cannot_close_before_resolving()
    {
        var t = NewTicket();

        var ex = Assert.Throws<TicketsDomainException>(() => t.Close());
        Assert.Contains("resolvido", ex.Message);
    }

    [Fact]
    public void Full_lifecycle_open_assign_resolve_close_reopen()
    {
        var t = NewTicket();
        t.AssignTo(Guid.NewGuid());
        t.Resolve();
        t.Close();

        Assert.Equal(TicketStatus.Closed, t.Status);
        Assert.NotNull(t.ClosedAt);

        t.Reopen();

        Assert.Equal(TicketStatus.Open, t.Status);
        Assert.Null(t.ClosedAt);
    }

    [Fact]
    public void Closed_ticket_cannot_be_changed_until_reopened()
    {
        var t = NewTicket();
        t.Resolve();
        t.Close();

        Assert.Throws<TicketsDomainException>(() => t.AssignTo(Guid.NewGuid()));
        Assert.Throws<TicketsDomainException>(() => t.Resolve());
    }

    [Fact]
    public void Cannot_reopen_a_ticket_that_is_still_open()
    {
        var t = NewTicket();

        Assert.Throws<TicketsDomainException>(() => t.Reopen());
    }
}

public class SlaTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static Ticket TicketCreatedMinutesAgo(double minutes, TicketPriority p = TicketPriority.Urgent)
    {
        var t = Ticket.Open(Guid.NewGuid(), Guid.NewGuid(), "x", "", p);
        typeof(Ticket).GetProperty(nameof(Ticket.CreatedAt))!.SetValue(t, Now.AddMinutes(-minutes));
        return t;
    }

    [Fact]
    public void Unattended_ticket_past_the_limit_is_overdue() =>
        Assert.True(TicketCreatedMinutesAgo(20).IsSlaOverdue(Now, TimeSpan.FromMinutes(15)));

    [Fact]
    public void Ticket_within_the_limit_is_not_overdue() =>
        Assert.False(TicketCreatedMinutesAgo(5).IsSlaOverdue(Now, TimeSpan.FromMinutes(15)));

    [Fact]
    public void Assigned_ticket_is_never_overdue()
    {
        var t = TicketCreatedMinutesAgo(120);
        t.AssignTo(Guid.NewGuid());

        Assert.False(t.IsSlaOverdue(Now, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Ticket_is_only_flagged_once()
    {
        var t = TicketCreatedMinutesAgo(120);
        Assert.True(t.IsSlaOverdue(Now, TimeSpan.FromMinutes(15)));

        t.MarkSlaBreached(Now);

        Assert.False(t.IsSlaOverdue(Now.AddHours(1), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Policy_gives_shorter_deadlines_to_higher_priorities()
    {
        var policy = new SlaPolicy();

        Assert.True(policy.LimitFor(TicketPriority.Urgent) < policy.LimitFor(TicketPriority.High));
        Assert.True(policy.LimitFor(TicketPriority.High) < policy.LimitFor(TicketPriority.Medium));
        Assert.True(policy.LimitFor(TicketPriority.Medium) < policy.LimitFor(TicketPriority.Low));
        Assert.Equal(policy.LimitFor(TicketPriority.Urgent), policy.ShortestLimit);
    }
}

public class UserTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static User NewUser() => User.Create(Guid.NewGuid(), "Ana", "  Ana@Acme.COM ", "hash", UserRole.Agent);

    [Fact]
    public void Email_is_normalized() => Assert.Equal("ana@acme.com", NewUser().Email);

    [Fact]
    public void Account_locks_after_five_failed_attempts()
    {
        var u = NewUser();

        for (var i = 0; i < User.MaxFailedAttempts - 1; i++) u.RegisterFailedLogin(Now);
        Assert.False(u.IsLockedOut(Now));

        u.RegisterFailedLogin(Now);

        Assert.True(u.IsLockedOut(Now));
        Assert.False(u.IsLockedOut(Now + User.LockoutDuration + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Successful_login_resets_the_counter()
    {
        var u = NewUser();
        for (var i = 0; i < 4; i++) u.RegisterFailedLogin(Now);

        u.RegisterSuccessfulLogin();
        u.RegisterFailedLogin(Now);

        Assert.Equal(1, u.FailedLoginCount);
        Assert.False(u.IsLockedOut(Now));
    }

    [Fact]
    public void Failures_after_lockout_expired_start_a_new_count()
    {
        var u = NewUser();
        for (var i = 0; i < User.MaxFailedAttempts; i++) u.RegisterFailedLogin(Now);

        u.RegisterFailedLogin(Now + User.LockoutDuration + TimeSpan.FromMinutes(1));

        Assert.Equal(1, u.FailedLoginCount);
    }
}

public class TenantTests
{
    [Fact]
    public void New_tenant_is_provisioning_and_inactive()
    {
        var t = Tenant.Create("Acme");

        Assert.Equal(TenantStatus.Provisioning, t.Status);
        Assert.False(t.IsActive);
    }

    [Fact]
    public void Activation_makes_it_usable()
    {
        var t = Tenant.Create("Acme");

        t.Activate();

        Assert.True(t.IsActive);
    }

    [Fact]
    public void Failure_keeps_the_reason()
    {
        var t = Tenant.Create("Acme");

        t.Fail("Nome duplicado");

        Assert.Equal(TenantStatus.Failed, t.Status);
        Assert.Equal("Nome duplicado", t.FailureReason);
    }

    [Fact]
    public void Company_name_is_validated() =>
        Assert.Throws<IdentityDomainException>(() => Tenant.Create(new string('x', 151)));

    [Theory]
    [InlineData("Acme Ltda", "acme ltda")]
    [InlineData("  ACME   Ltda ", "acme ltda")]
    [InlineData("acme\tltda", "acme ltda")]
    public void Profile_names_are_normalized_so_duplicates_are_detected(string input, string expected) =>
        Assert.Equal(expected, TenantProfile.Normalize(input));
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curta1")]
    [InlineData("somenteletrasaqui")]
    [InlineData("12345678901234")]
    public void Weak_passwords_are_rejected(string? password) =>
        Assert.Throws<IdentityDomainException>(() => PasswordPolicy.Validate(password));

    [Fact]
    public void Very_long_passwords_are_rejected() =>
        Assert.Throws<IdentityDomainException>(() => PasswordPolicy.Validate(new string('a', 120) + "1234567890"));

    [Fact]
    public void Reasonable_password_is_accepted() => PasswordPolicy.Validate("senhaForte123");
}

public class EventContractTests
{
    [Fact]
    public void Deterministic_event_ids_are_stable_and_distinct()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(EventIds.FromKey($"x:{a}"), EventIds.FromKey($"x:{a}"));
        Assert.NotEqual(EventIds.FromKey($"x:{a}"), EventIds.FromKey($"x:{b}"));
    }

    [Fact]
    public void Sla_breach_for_the_same_ticket_always_has_the_same_event_id()
    {
        var ticket = Guid.NewGuid();

        var first = TicketSlaBreached.Create(Guid.NewGuid(), ticket, Guid.NewGuid(), "t", "Urgent", 20);
        var second = TicketSlaBreached.Create(Guid.NewGuid(), ticket, Guid.NewGuid(), "t", "Urgent", 45);

        Assert.Equal(first.EventId, second.EventId);
    }

    [Fact]
    public void Ordinary_events_get_unique_ids()
    {
        var a = TicketCreated.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "t", "Low");
        var b = TicketCreated.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "t", "Low");

        Assert.NotEqual(a.EventId, b.EventId);
    }

    [Fact]
    public void Event_names_follow_the_service_dot_fact_convention()
    {
        Assert.Equal("identity.user-registered", UserRegistered.EventName);
        Assert.Equal("tickets.ticket-created", TicketCreated.EventName);
        Assert.Equal("tenants.tenant-provisioned", TenantProvisioned.EventName);
    }

    [Fact]
    public void Tenant_registered_event_has_no_credentials()
    {
        var names = typeof(TenantRegistered).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(names, n => n.Contains("password") || n.Contains("hash") || n.Contains("secret"));
    }
}
