using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HelpDeskFlow.IntegrationTests;

/// <summary>Limites do plano: o Tenants define (plano Free = 5 usuários) e o Identity aplica ao cadastrar pessoas.</summary>
[Collection(StackCollection.Name)]
public class PlanLimitTests(StackFixture stack)
{
    private Task<HttpResponseMessage> PostUserAsync(Company company, string role = "Agent") =>
        stack.Identity.SendAsync(company.Admin.Token, HttpMethod.Post, "/api/users", new
        {
            name = $"Pessoa {StackFixture.Unique("p")}",
            email = $"{StackFixture.Unique("u")}@test.com",
            password = StackFixture.Password,
            role
        });

    [Fact]
    public async Task The_plan_limit_travels_from_Tenants_to_Identity_through_the_provisioning_event()
    {
        var company = await stack.CreateCompanyAsync();

        var profile = await (await stack.Tenants.SendAsync(company.Admin.Token, HttpMethod.Get, "/api/tenants/me"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(5, profile.GetProperty("maxUsers").GetInt32()); // o plano Free, definido pelo Tenants
        // ...e o Identity o aplica: o administrador + 4 pessoas cabem; a 5ª pessoa não.
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Created, (await PostUserAsync(company)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostUserAsync(company)).StatusCode);
    }

    [Fact]
    public async Task Going_over_the_limit_returns_409_with_a_message_for_the_admin()
    {
        var company = await stack.CreateCompanyAsync();
        for (var i = 0; i < 4; i++) await stack.AddUserAsync(company, "Agent");

        var response = await PostUserAsync(company, "Customer");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Limite de usuários do plano atingido (5)", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_blocked_user_was_not_created_at_all()
    {
        var company = await stack.CreateCompanyAsync();
        for (var i = 0; i < 4; i++) await stack.AddUserAsync(company, "Agent");
        await PostUserAsync(company); // 409

        var list = await (await stack.Identity.SendAsync(company.Admin.Token, HttpMethod.Get, "/api/users"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(5, list.GetArrayLength()); // não vazou nenhum "usuário fantasma"
    }

    [Fact]
    public async Task Companies_have_independent_limits()
    {
        var full = await stack.CreateCompanyAsync("Cheia");
        for (var i = 0; i < 4; i++) await stack.AddUserAsync(full, "Agent");
        var other = await stack.CreateCompanyAsync("Outra");

        Assert.Equal(HttpStatusCode.Conflict, (await PostUserAsync(full)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostUserAsync(other)).StatusCode); // a lotação de uma não afeta a outra
    }

    [Fact]
    public async Task Simultaneous_requests_for_the_last_slot_let_exactly_one_through()
    {
        var company = await stack.CreateCompanyAsync();
        for (var i = 0; i < 3; i++) await stack.AddUserAsync(company, "Agent"); // admin + 3 = 4 de 5: resta UMA vaga

        // Seis cadastros AO MESMO TEMPO disputando essa vaga.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostUserAsync(company)));
        var statuses = responses.Select(r => r.StatusCode).ToList();

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created)); // só um entrou
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Conflict)); // os outros receberam um 409 claro, nunca um 500

        var list = await (await stack.Identity.SendAsync(company.Admin.Token, HttpMethod.Get, "/api/users"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, list.GetArrayLength()); // o limite jamais é furado
    }
}
