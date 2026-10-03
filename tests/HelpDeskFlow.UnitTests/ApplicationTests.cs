using HelpDeskFlow.Contracts;
using Identity.Application;
using Identity.Domain;
using Microsoft.Extensions.Time.Testing;
using Tickets.Application;
using Tickets.Domain.Enums;
using TicketsDomainException = Tickets.Domain.DomainException;

namespace HelpDeskFlow.UnitTests;

public class AuthServiceTests
{
    private const string Password = "senhaForte123";

    private readonly Timeline _timeline = new();
    private readonly FakeUserRepository _users = new();
    private readonly FakeTenantRepository _tenants = new();
    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly FakeHasher _hasher = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingPublisher _events;
    private readonly FakeUnitOfWork _uow;
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        _events = new RecordingPublisher(_timeline);
        _uow = new FakeUnitOfWork(_timeline);
        _sut = new AuthService(_users, _tenants, _refreshTokens, _uow, _hasher, new FakeTokenService(_clock), _clock, _events);
    }

    /// <summary>Cria uma empresa já ATIVA com um usuário, como se a saga tivesse concluído.</summary>
    private async Task<User> SeedActiveUserAsync(string email = "ana@acme.com", UserRole role = UserRole.Agent, int maxUsers = 5)
    {
        var tenant = Tenant.Create("Acme");
        tenant.Activate(maxUsers);
        _tenants.Items.Add(tenant);
        var user = User.Create(tenant.Id, "Ana", email, _hasher.Hash(Password), role);
        await _users.AddAsync(user, default);
        return user;
    }

    // ───── Registro (passo 1 da saga) ─────

    [Fact]
    public async Task Register_creates_a_provisioning_tenant_and_does_not_issue_tokens()
    {
        var result = await _sut.RegisterTenantAsync(new("Acme", "Ana", "ana@acme.com", Password), default);

        Assert.Equal("Provisioning", result.Status);
        Assert.Single(_tenants.Items);
        Assert.False(_tenants.Items[0].IsActive);
        Assert.Empty(_refreshTokens.Items);
    }

    [Fact]
    public async Task Register_publishes_TenantRegistered_BEFORE_saving_so_both_commit_together()
    {
        await _sut.RegisterTenantAsync(new("Acme", "Ana", "ana@acme.com", Password), default);

        Assert.Equal(["publish:identity.tenant-registered", "save"], _timeline.Entries);
    }

    [Fact]
    public async Task Registration_event_never_carries_the_password()
    {
        await _sut.RegisterTenantAsync(new("Acme", "Ana", "ana@acme.com", Password), default);

        var json = System.Text.Json.JsonSerializer.Serialize(_events.Events[0], _events.Events[0].GetType());
        Assert.DoesNotContain(Password, json);
        Assert.DoesNotContain("hash:", json);
    }

    [Fact]
    public async Task Register_does_not_announce_the_admin_user_yet()
    {
        await _sut.RegisterTenantAsync(new("Acme", "Ana", "ana@acme.com", Password), default);

        Assert.Empty(_events.Of<UserRegistered>());
    }

    [Fact]
    public async Task Duplicate_email_is_a_conflict_regardless_of_case()
    {
        await _sut.RegisterTenantAsync(new("Acme", "Ana", "ana@acme.com", Password), default);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.RegisterTenantAsync(new("Outra", "Ana2", "ANA@ACME.COM", Password), default));
    }

    [Fact]
    public async Task Weak_password_is_rejected_before_anything_is_saved()
    {
        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.RegisterTenantAsync(new("Acme", "Ana", "ana@acme.com", "123"), default));

        Assert.Equal(0, _uow.Saves);
    }

    // ───── Login ─────

    [Fact]
    public async Task Login_fails_while_the_tenant_is_still_provisioning()
    {
        await _sut.RegisterTenantAsync(new("Acme", "Ana", "ana@acme.com", Password), default);

        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            _sut.LoginAsync(new("ana@acme.com", Password), default));
    }

    [Fact]
    public async Task Login_succeeds_for_an_active_tenant_and_issues_tokens()
    {
        await SeedActiveUserAsync();

        var response = await _sut.LoginAsync(new("ANA@acme.com", Password), default);

        Assert.StartsWith("jwt-for-", response.AccessToken);
        Assert.Equal(900, response.ExpiresInSeconds);
        Assert.Single(_refreshTokens.Items);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_look_identical_to_the_caller()
    {
        await SeedActiveUserAsync();

        var unknown = await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            _sut.LoginAsync(new("nobody@acme.com", Password), default));
        var wrong = await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            _sut.LoginAsync(new("ana@acme.com", "errada12345"), default));

        Assert.Equal(unknown.Message, wrong.Message);
    }

    [Fact]
    public async Task Unknown_email_still_burns_hashing_time_to_prevent_user_enumeration()
    {
        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            _sut.LoginAsync(new("nobody@acme.com", Password), default));

        Assert.Equal(1, _hasher.SimulatedVerifications);
    }

    [Fact]
    public async Task Account_is_locked_after_five_wrong_passwords_even_with_the_right_one()
    {
        await SeedActiveUserAsync();

        for (var i = 0; i < User.MaxFailedAttempts; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.LoginAsync(new("ana@acme.com", "errada12345"), default));

        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.LoginAsync(new("ana@acme.com", Password), default));
    }

    [Fact]
    public async Task Lock_expires_after_the_lockout_duration()
    {
        await SeedActiveUserAsync();
        for (var i = 0; i < User.MaxFailedAttempts; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.LoginAsync(new("ana@acme.com", "errada12345"), default));

        _clock.Advance(User.LockoutDuration + TimeSpan.FromSeconds(1));

        var response = await _sut.LoginAsync(new("ana@acme.com", Password), default);
        Assert.NotNull(response.AccessToken);
    }

    // ───── Refresh tokens ─────

    [Fact]
    public async Task Refresh_rotates_the_token_and_invalidates_the_old_one()
    {
        await SeedActiveUserAsync();
        var login = await _sut.LoginAsync(new("ana@acme.com", Password), default);

        var rotated = await _sut.RefreshAsync(new(login.RefreshToken), default);

        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        Assert.True(_refreshTokens.Items.First(t => t.TokenHash == "h:" + login.RefreshToken).IsRevoked);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_every_session_of_the_user()
    {
        await SeedActiveUserAsync();
        var login = await _sut.LoginAsync(new("ana@acme.com", Password), default);
        var rotated = await _sut.RefreshAsync(new(login.RefreshToken), default);

        // Alguém apresenta o token ANTIGO de novo: sinal de roubo.
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.RefreshAsync(new(login.RefreshToken), default));

        Assert.All(_refreshTokens.Items, t => Assert.True(t.IsRevoked));
        // ...inclusive o token novo, que o ladrão ou o dono legítimo poderiam estar usando.
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.RefreshAsync(new(rotated.RefreshToken), default));
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        await SeedActiveUserAsync();
        var login = await _sut.LoginAsync(new("ana@acme.com", Password), default);

        _clock.Advance(TimeSpan.FromDays(8));

        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.RefreshAsync(new(login.RefreshToken), default));
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        await SeedActiveUserAsync();
        var login = await _sut.LoginAsync(new("ana@acme.com", Password), default);

        await _sut.LogoutAsync(new(login.RefreshToken), default);

        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.RefreshAsync(new(login.RefreshToken), default));
    }

    // ───── Usuários ─────

    [Fact]
    public async Task Creating_a_user_publishes_UserRegistered_before_saving()
    {
        var admin = await SeedActiveUserAsync(role: UserRole.Admin);

        await _sut.CreateUserAsync(admin.TenantId, new("Carla", "carla@acme.com", Password, UserRole.Customer), default);

        Assert.Equal(["publish:identity.user-registered", "save"], _timeline.Entries);
        var evt = Assert.Single(_events.Of<UserRegistered>());
        Assert.Equal("Customer", evt.Role);
        Assert.Equal(admin.TenantId, evt.TenantId);
    }

    [Fact]
    public async Task Creating_users_stops_exactly_at_the_plan_limit()
    {
        var admin = await SeedActiveUserAsync(role: UserRole.Admin, maxUsers: 3); // admin (1) + 2 vagas

        await _sut.CreateUserAsync(admin.TenantId, new("Um", "um@acme.com", Password, UserRole.Agent), default);
        await _sut.CreateUserAsync(admin.TenantId, new("Dois", "dois@acme.com", Password, UserRole.Customer), default);
        var blocked = await Assert.ThrowsAsync<PlanLimitExceededException>(() =>
            _sut.CreateUserAsync(admin.TenantId, new("Tres", "tres@acme.com", Password, UserRole.Customer), default));

        Assert.Contains("(3)", blocked.Message);
        Assert.Contains("upgrade", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsAssignableFrom<ConflictException>(blocked); // vira HTTP 409
    }

    [Fact]
    public async Task A_blocked_creation_saves_nothing_and_announces_nothing()
    {
        var admin = await SeedActiveUserAsync(role: UserRole.Admin, maxUsers: 1); // só o admin cabe
        var usersBefore = _users.Items.Count;
        _timeline.Entries.Clear();

        await Assert.ThrowsAsync<PlanLimitExceededException>(() =>
            _sut.CreateUserAsync(admin.TenantId, new("Extra", "extra@acme.com", Password, UserRole.Agent), default));

        Assert.Equal(usersBefore, _users.Items.Count);
        Assert.Empty(_timeline.Entries); // nem gravou, nem publicou UserRegistered
    }

    [Fact]
    public async Task Invalid_role_is_rejected()
    {
        var admin = await SeedActiveUserAsync(role: UserRole.Admin);

        await Assert.ThrowsAsync<DomainException>(() =>
            _sut.CreateUserAsync(admin.TenantId, new("X", "x@acme.com", Password, (UserRole)99), default));
    }

    [Fact]
    public async Task Listing_only_returns_users_of_the_given_tenant()
    {
        var a = await SeedActiveUserAsync("a@a.com");
        await SeedActiveUserAsync("b@b.com"); // outra empresa

        var list = await _sut.ListUsersAsync(a.TenantId, default);

        Assert.Single(list);
        Assert.Equal("a@a.com", list[0].Email);
    }
}

public class TicketServiceTests
{
    private readonly Timeline _timeline = new();
    private readonly FakeTicketRepository _repo;
    private readonly FakeKnownUsers _known = new();
    private readonly RecordingPublisher _events;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    public TicketServiceTests()
    {
        _repo = new FakeTicketRepository(_timeline);
        _events = new RecordingPublisher(_timeline);
    }

    private TicketService Sut(bool customer = false) =>
        new(_repo, _known, new FakeCurrentUser(_tenant, _user, customer), _events);

    [Fact]
    public async Task Creating_a_ticket_publishes_the_event_before_saving()
    {
        var created = await Sut().CreateAsync(new("Servidor fora", "urgente", TicketPriority.Urgent), default);

        Assert.Equal(["publish:tickets.ticket-created", "save"], _timeline.Entries);
        var evt = Assert.Single(_events.Of<TicketCreated>());
        Assert.Equal(created.Id, evt.TicketId);
        Assert.Equal(_tenant, evt.TenantId);
        Assert.Equal(_user, evt.RequesterId);
    }

    [Fact]
    public async Task Ticket_is_created_for_the_current_tenant_and_user()
    {
        await Sut().CreateAsync(new("x", "", TicketPriority.Low), default);

        var ticket = Assert.Single(_repo.Items);
        Assert.Equal(_tenant, ticket.TenantId);
        Assert.Equal(_user, ticket.RequesterId);
    }

    [Fact]
    public async Task Assignee_must_be_staff_of_the_same_tenant()
    {
        var created = await Sut().CreateAsync(new("x", "", TicketPriority.Low), default);
        var stranger = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<TicketsDomainException>(() =>
            Sut().AssignAsync(created.Id, new(stranger), default));

        Assert.Contains("Responsável inválido", ex.Message);
        Assert.Empty(_events.Of<TicketAssigned>());
    }

    [Fact]
    public async Task Staff_of_ANOTHER_tenant_cannot_be_assigned()
    {
        var created = await Sut().CreateAsync(new("x", "", TicketPriority.Low), default);
        var outsider = Guid.NewGuid();
        _known.AddStaff(Guid.NewGuid(), outsider); // é equipe, mas de outra empresa

        await Assert.ThrowsAsync<TicketsDomainException>(() => Sut().AssignAsync(created.Id, new(outsider), default));
    }

    [Fact]
    public async Task Valid_assignment_publishes_TicketAssigned_with_the_assignee()
    {
        var created = await Sut().CreateAsync(new("x", "", TicketPriority.Low), default);
        var agent = Guid.NewGuid();
        _known.AddStaff(_tenant, agent);
        _timeline.Entries.Clear();

        var result = await Sut().AssignAsync(created.Id, new(agent), default);

        Assert.Equal(TicketStatus.InProgress, result!.Status);
        Assert.Equal(["publish:tickets.ticket-assigned", "save"], _timeline.Entries);
        Assert.Equal(agent, Assert.Single(_events.Of<TicketAssigned>()).AssigneeId);
    }

    [Fact]
    public async Task Resolving_publishes_TicketResolved_with_who_resolved_it()
    {
        var created = await Sut().CreateAsync(new("x", "", TicketPriority.Low), default);

        await Sut().ResolveAsync(created.Id, default);

        var evt = Assert.Single(_events.Of<TicketResolved>());
        Assert.Equal(_user, evt.ResolvedBy);
    }

    [Fact]
    public async Task Ticket_responses_carry_requester_and_assignee_names()
    {
        var agent = Guid.NewGuid();
        _known.AddStaff(_tenant, agent);
        _known.AddName(_user, "Carla Cliente");
        _known.AddName(agent, "Alex Agente");
        var created = await Sut().CreateAsync(new("x", "", TicketPriority.Low), default);
        Assert.Equal("Carla Cliente", created.RequesterName);
        Assert.Null(created.AssigneeName);

        await Sut().AssignAsync(created.Id, new(agent), default);
        var listed = Assert.Single(await Sut().ListAsync(default));

        Assert.Equal("Carla Cliente", listed.RequesterName);
        Assert.Equal("Alex Agente", listed.AssigneeName);
    }

    [Fact]
    public async Task Unknown_users_leave_names_empty_instead_of_failing()
    {
        var created = await Sut().CreateAsync(new("x", "", TicketPriority.Low), default);

        Assert.Null(created.RequesterName);
    }

    [Fact]
    public async Task Staff_list_only_contains_staff_of_the_current_tenant()
    {
        var mine = Guid.NewGuid();
        _known.AddStaff(_tenant, mine);
        _known.AddName(mine, "Minha equipe");
        var theirs = Guid.NewGuid();
        _known.AddStaff(Guid.NewGuid(), theirs);

        var staff = await Sut().ListStaffAsync(default);

        Assert.Equal(mine, Assert.Single(staff).Id);
    }

    [Fact]
    public async Task Unknown_ticket_returns_null_and_publishes_nothing()
    {
        var result = await Sut().ResolveAsync(Guid.NewGuid(), default);

        Assert.Null(result);
        Assert.Empty(_events.Of<TicketResolved>());
    }

    [Fact]
    public async Task Failed_business_rule_does_not_save_or_publish()
    {
        var created = await Sut().CreateAsync(new("x", "", TicketPriority.Low), default);
        _timeline.Entries.Clear();

        await Assert.ThrowsAsync<TicketsDomainException>(() => Sut().CloseAsync(created.Id, default)); // não resolvido

        Assert.Empty(_timeline.Entries);
    }
}

public class SlaServiceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTicketRepository _repo = new();
    private readonly RecordingPublisher _events = new();
    private readonly SlaPolicy _policy = new() { UrgentMinutes = 15, HighMinutes = 60, MediumMinutes = 240, LowMinutes = 1440 };

    private SlaService Sut() => new(_repo, _events, _policy, _clock);

    private Tickets.Domain.Entities.Ticket Seed(TicketPriority priority, double ageMinutes, bool assigned = false)
    {
        var t = Tickets.Domain.Entities.Ticket.Open(Guid.NewGuid(), Guid.NewGuid(), "t", "", priority);
        typeof(Tickets.Domain.Entities.Ticket).GetProperty(nameof(t.CreatedAt))!
            .SetValue(t, _clock.GetUtcNow().UtcDateTime.AddMinutes(-ageMinutes));
        if (assigned) t.AssignTo(Guid.NewGuid());
        _repo.Items.Add(t);
        return t;
    }

    [Fact]
    public async Task Only_tickets_past_THEIR_priority_limit_are_overdue()
    {
        var urgentLate = Seed(TicketPriority.Urgent, 20);
        Seed(TicketPriority.Urgent, 5);
        Seed(TicketPriority.Low, 60);              // Low tem 24 h: ainda no prazo
        var highLate = Seed(TicketPriority.High, 90);

        var overdue = await Sut().FindOverdueAsync(default);

        Assert.Equal(2, overdue.Count);
        Assert.Contains(urgentLate.Id, overdue);
        Assert.Contains(highLate.Id, overdue);
    }

    [Fact]
    public async Task Assigned_tickets_are_ignored()
    {
        Seed(TicketPriority.Urgent, 120, assigned: true);

        Assert.Empty(await Sut().FindOverdueAsync(default));
    }

    [Fact]
    public async Task Breach_marks_the_ticket_and_publishes_one_event_with_a_deterministic_id()
    {
        var t = Seed(TicketPriority.Urgent, 30);

        Assert.True(await Sut().BreachAsync(t.Id, default));

        Assert.NotNull(t.SlaBreachedAt);
        var evt = Assert.Single(_events.Of<TicketSlaBreached>());
        Assert.Equal(EventIds.FromKey($"sla-breached:{t.Id}"), evt.EventId);
        Assert.Equal(30, evt.MinutesWaiting);
        Assert.Equal(1, _repo.Saves);
    }

    [Fact]
    public async Task Breaching_twice_is_a_no_op()
    {
        var t = Seed(TicketPriority.Urgent, 30);
        await Sut().BreachAsync(t.Id, default);

        Assert.False(await Sut().BreachAsync(t.Id, default));

        Assert.Single(_events.Of<TicketSlaBreached>());
    }

    [Fact]
    public async Task Ticket_assigned_after_listing_is_rechecked_and_skipped()
    {
        var t = Seed(TicketPriority.Urgent, 30);
        t.AssignTo(Guid.NewGuid()); // atribuído entre a listagem e o processamento

        Assert.False(await Sut().BreachAsync(t.Id, default));
        Assert.Empty(_events.Events);
    }
}
