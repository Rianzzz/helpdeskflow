using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HelpDeskFlow.IntegrationTests;

[Collection(StackCollection.Name)]
public class OnboardingSagaTests(StackFixture stack)
{
    [Fact]
    public async Task Registering_a_company_provisions_it_end_to_end()
    {
        var name = StackFixture.Unique("Saga");
        var email = $"{StackFixture.Unique("ana")}@test.com";

        var tenantId = await stack.RegisterTenantAsync(name, "Ana", email);
        var status = await stack.WaitForTenantAsync(tenantId, "Active");

        Assert.Equal("Active", status.GetProperty("status").GetString());

        // Identity: o administrador já consegue entrar.
        var token = await stack.LoginAsync(email);
        Assert.NotNull(token);

        // Tenants: o perfil e o plano foram criados pelo OUTRO serviço, via evento.
        var profile = await (await stack.Tenants.SendAsync(token, HttpMethod.Get, "/api/tenants/me"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name, profile.GetProperty("name").GetString());
        Assert.Equal("Free", profile.GetProperty("plan").GetString());

        // Notifications: boas-vindas ao administrador, também via evento.
        var admin = new Person(Guid.Empty, email, token!, "Admin");
        await Wait.UntilAsync(async () => (await stack.NotificationSubjectsAsync(admin)).Any(s => s.StartsWith("Bem-vindo")),
            "notificação de boas-vindas");
    }

    [Fact]
    public async Task Registration_does_not_return_tokens()
    {
        var response = await stack.Identity.PostAsJsonAsync("/api/auth/register-tenant", new
        {
            companyName = StackFixture.Unique("SemToken"),
            adminName = "Ana",
            email = $"{StackFixture.Unique("ana")}@test.com",
            password = StackFixture.Password
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", body);
        Assert.DoesNotContain("refreshToken", body);
    }

    [Fact]
    public async Task Duplicate_company_name_is_rejected_compensated_and_the_email_is_freed()
    {
        var first = await stack.CreateCompanyAsync("Duplicada");

        // Mesmo nome, com caixa e espaços diferentes: continua sendo "a mesma empresa".
        var email = $"{StackFixture.Unique("beto")}@test.com";
        var secondId = await stack.RegisterTenantAsync($"  {first.Name.ToUpperInvariant()}  ", "Beto", email);
        var status = await stack.WaitForTenantAsync(secondId, "Failed", "Active");

        Assert.Equal("Failed", status.GetProperty("status").GetString());
        Assert.Contains("Já existe", status.GetProperty("failureReason").GetString());

        // Compensação: o administrador provisório foi desfeito, então não consegue entrar...
        Assert.Null(await stack.LoginAsync(email));

        // ...e o e-mail ficou livre para uma nova tentativa com outro nome.
        var retryId = await stack.RegisterTenantAsync(StackFixture.Unique("Outra"), "Beto", email);
        var retry = await stack.WaitForTenantAsync(retryId, "Active", "Failed");
        Assert.Equal("Active", retry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Reserved_company_name_is_rejected()
    {
        var id = await stack.RegisterTenantAsync("Admin", "Zeca", $"{StackFixture.Unique("z")}@test.com");

        var status = await stack.WaitForTenantAsync(id, "Failed", "Active");

        Assert.Equal("Failed", status.GetProperty("status").GetString());
        Assert.Contains("reservado", status.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task Failed_company_never_leaks_its_admin_to_other_services()
    {
        var first = await stack.CreateCompanyAsync("Dono");
        var email = $"{StackFixture.Unique("intruso")}@test.com";
        var id = await stack.RegisterTenantAsync(first.Name, "Intruso", email);
        await stack.WaitForTenantAsync(id, "Failed");

        // O administrador de uma empresa que falhou não pode entrar em nenhum serviço.
        Assert.Null(await stack.LoginAsync(email));
    }

    [Fact]
    public async Task Unknown_tenant_status_is_404()
    {
        var response = await stack.Identity.GetAsync($"/api/auth/tenants/{Guid.NewGuid()}/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
