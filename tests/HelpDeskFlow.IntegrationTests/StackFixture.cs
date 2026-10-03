using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace HelpDeskFlow.IntegrationTests;

/// <summary>
/// Sobe a "plataforma" inteira DENTRO do processo de teste: PostgreSQL e RabbitMQ reais (contêineres) e os quatro
/// serviços (Identity, Tickets, Tenants, Notifications) conversando por eventos de verdade. Só o Gateway fica de fora
/// (é um proxy; as regras de negócio estão nos serviços).
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    public const string Password = "senhaForte123";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").WithDatabase("postgres").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4").WithUsername("helpdesk").WithPassword("helpdesk_dev").Build();

    private WebApplicationFactory<Identity.Api.ApiMarker> _identity = null!;
    private WebApplicationFactory<Tickets.Api.ApiMarker> _tickets = null!;
    private WebApplicationFactory<Tenants.Api.ApiMarker> _tenants = null!;
    private WebApplicationFactory<Notifications.Api.ApiMarker> _notifications = null!;

    public HttpClient Identity { get; private set; } = null!;
    public HttpClient Tickets { get; private set; } = null!;
    public HttpClient Tenants { get; private set; } = null!;
    public HttpClient Notifications { get; private set; } = null!;

    public IServiceProvider NotificationsServices => _notifications.Services;
    public string RabbitHost => _rabbit.Hostname;
    public int RabbitPort => _rabbit.GetMappedPublicPort(5672);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        // Um banco por serviço, como em produção.
        foreach (var db in new[] { "identity", "tickets", "tenants", "notifications" })
            await ExecuteAsync($"CREATE DATABASE helpdesk_{db}");

        // Configuração por variáveis de ambiente (têm precedência sobre os appsettings dos serviços).
        Env("ConnectionStrings__IdentityDb", Connection("helpdesk_identity"));
        Env("ConnectionStrings__TicketsDb", Connection("helpdesk_tickets"));
        Env("ConnectionStrings__TenantsDb", Connection("helpdesk_tenants"));
        Env("ConnectionStrings__NotificationsDb", Connection("helpdesk_notifications"));
        Env("RabbitMq__Host", RabbitHost);
        Env("RabbitMq__Port", RabbitPort.ToString());
        Env("RabbitMq__User", "helpdesk");
        Env("RabbitMq__Password", "helpdesk_dev");
        Env("Jwt__SigningKey", "TEST-ONLY-chave-de-teste-com-mais-de-trinta-e-dois-bytes");
        Env("RateLimiting__AuthPermitPerMinute", "100000");
        // SLA acelerado: chamados Urgent estouram em ~2 s, verificados a cada 1 s.
        Env("Sla__UrgentMinutes", "0.03");
        Env("Sla__CheckIntervalSeconds", "1");

        // Consumidores primeiro: as filas precisam existir antes de os eventos serem publicados.
        _notifications = new WebApplicationFactory<Notifications.Api.ApiMarker>();
        _tickets = new WebApplicationFactory<Tickets.Api.ApiMarker>();
        _tenants = new WebApplicationFactory<Tenants.Api.ApiMarker>();
        _identity = new WebApplicationFactory<Identity.Api.ApiMarker>();

        Notifications = _notifications.CreateClient();
        Tickets = _tickets.CreateClient();
        Tenants = _tenants.CreateClient();
        Identity = _identity.CreateClient();

        // Pequena folga para os consumidores declararem suas filas no RabbitMQ.
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    public async Task DisposeAsync()
    {
        foreach (var c in new[] { Identity, Tickets, Tenants, Notifications }) c?.Dispose();
        if (_identity is not null) await _identity.DisposeAsync();
        if (_tenants is not null) await _tenants.DisposeAsync();
        if (_tickets is not null) await _tickets.DisposeAsync();
        if (_notifications is not null) await _notifications.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static void Env(string key, string value) => Environment.SetEnvironmentVariable(key, value);

    private string Connection(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ───────────── Ajudantes de cenário ─────────────

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, 40)];

    public async Task<Guid> RegisterTenantAsync(string company, string adminName, string email)
    {
        var response = await Identity.PostAsJsonAsync("/api/auth/register-tenant",
            new { companyName = company, adminName, email, password = Password });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); // 202: provisionamento em segundo plano
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("tenantId").GetGuid();
    }

    public async Task<JsonElement> WaitForTenantAsync(Guid tenantId, params string[] statuses)
    {
        JsonElement last = default;
        await Wait.UntilAsync(async () =>
        {
            last = await Identity.GetFromJsonAsync<JsonElement>($"/api/auth/tenants/{tenantId}/status");
            return statuses.Contains(last.GetProperty("status").GetString());
        }, $"empresa {tenantId} atingir {string.Join("/", statuses)}");
        return last;
    }

    public async Task<string?> LoginAsync(string email, string password = Password)
    {
        var response = await Identity.PostAsJsonAsync("/api/auth/login", new { email, password });
        if (!response.IsSuccessStatusCode) return null;
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
    }

    public async Task<JsonElement> LoginRawAsync(string email, string password = Password)
    {
        var response = await Identity.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Cadastra uma empresa, espera a saga concluir e entra como administrador.</summary>
    public async Task<Company> CreateCompanyAsync(string prefix = "Empresa")
    {
        var name = Unique(prefix);
        var email = $"{Unique("admin")}@test.com";
        var tenantId = await RegisterTenantAsync(name, "Admin", email);
        await WaitForTenantAsync(tenantId, "Active");

        var login = await LoginRawAsync(email);
        var token = login.GetProperty("accessToken").GetString()!;
        var adminId = login.GetProperty("user").GetProperty("id").GetGuid();
        return new Company(tenantId, name, new Person(adminId, email, token, "Admin"));
    }

    public async Task<Person> AddUserAsync(Company company, string role)
    {
        var email = $"{Unique(role.ToLowerInvariant())}@test.com";
        var response = await Identity.SendAsync(company.Admin.Token, HttpMethod.Post, "/api/users",
            new { name = role, email, password = Password, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var login = await LoginRawAsync(email);
        return new Person(login.GetProperty("user").GetProperty("id").GetGuid(), email,
            login.GetProperty("accessToken").GetString()!, role);
    }

    public async Task<Guid> CreateTicketAsync(Person who, string title, string priority = "Low")
    {
        var response = await Tickets.SendAsync(who.Token, HttpMethod.Post, "/api/tickets", new { title, description = "", priority });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public async Task<List<string>> NotificationSubjectsAsync(Person who)
    {
        var list = await (await Notifications.SendAsync(who.Token, HttpMethod.Get, "/api/notifications"))
            .Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().Select(n => n.GetProperty("subject").GetString()!).ToList();
    }
}

public record Person(Guid Id, string Email, string Token, string Role);
public record Company(Guid TenantId, string Name, Person Admin);

public static class HttpExtensions
{
    public static Task<HttpResponseMessage> SendAsync(
        this HttpClient client, string? token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
}

public static class Wait
{
    /// <summary>
    /// Em sistemas orientados a eventos o resultado aparece "logo depois", não instantaneamente.
    /// Em vez de dormir um tempo fixo (lento e frágil), repetimos a verificação até dar certo ou estourar o prazo.
    /// </summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int seconds = 40)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        Assert.Fail($"Tempo esgotado ({seconds}s) esperando: {what}");
    }
}

[CollectionDefinition(Name)]
public class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}
