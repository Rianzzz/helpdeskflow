using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HelpDeskFlow.IntegrationTests;

[Collection(StackCollection.Name)]
public class SecurityTests(StackFixture stack)
{
    [Theory]
    [InlineData("/api/tickets")]
    [InlineData("/api/notifications")]
    [InlineData("/api/tenants/me")]
    [InlineData("/api/users/me")]
    public async Task Protected_endpoints_reject_anonymous_requests(string url)
    {
        var client = url switch
        {
            "/api/tickets" => stack.Tickets,
            "/api/notifications" => stack.Notifications,
            "/api/tenants/me" => stack.Tenants,
            _ => stack.Identity
        };

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tampered_token_is_rejected_by_every_service()
    {
        var company = await stack.CreateCompanyAsync();
        var tampered = company.Admin.Token[..^3] + "abc";

        foreach (var (client, url) in new[] { (stack.Tickets, "/api/tickets"), (stack.Identity, "/api/users/me"),
                     (stack.Notifications, "/api/notifications"), (stack.Tenants, "/api/tenants/me") })
        {
            var response = await client.SendAsync(tampered, HttpMethod.Get, url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        // Token "plausível" (mesmas claims) assinado com uma chave que o servidor não conhece.
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes("uma-chave-qualquer-de-atacante-com-32-bytes-ou-mais!"));
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var forged = handler.CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "helpdeskflow-identity",
            Audience = "helpdeskflow-api",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.NewGuid().ToString(), ["tenant_id"] = Guid.NewGuid().ToString(), ["role"] = "Admin"
            },
            SigningCredentials = new(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256)
        });

        var response = await stack.Tickets.SendAsync(forged, HttpMethod.Get, "/api/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Customers_cannot_list_users_but_admins_can()
    {
        var company = await stack.CreateCompanyAsync();
        var customer = await stack.AddUserAsync(company, "Customer");

        var asCustomer = await stack.Identity.SendAsync(customer.Token, HttpMethod.Get, "/api/users");
        var asAdmin = await stack.Identity.SendAsync(company.Admin.Token, HttpMethod.Get, "/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, asCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
    }

    [Fact]
    public async Task Admin_only_sees_users_of_their_own_company()
    {
        var a = await stack.CreateCompanyAsync("A");
        var b = await stack.CreateCompanyAsync("B");
        await stack.AddUserAsync(b, "Agent");

        var list = await (await stack.Identity.SendAsync(a.Admin.Token, HttpMethod.Get, "/api/users"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Single(list.EnumerateArray()); // só o próprio admin
        Assert.All(list.EnumerateArray(), u => Assert.Equal(a.TenantId, u.GetProperty("tenantId").GetGuid()));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_return_the_same_answer()
    {
        var company = await stack.CreateCompanyAsync();

        var wrong = await stack.Identity.PostAsJsonAsync("/api/auth/login", new { email = company.Admin.Email, password = "errada12345" });
        var unknown = await stack.Identity.PostAsJsonAsync("/api/auth/login", new { email = "ninguem@test.com", password = "errada12345" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Account_is_locked_after_five_wrong_passwords_even_with_the_correct_one()
    {
        var company = await stack.CreateCompanyAsync();
        var agent = await stack.AddUserAsync(company, "Agent");

        for (var i = 0; i < 5; i++)
            Assert.Null(await stack.LoginAsync(agent.Email, "errada12345"));

        Assert.Null(await stack.LoginAsync(agent.Email)); // bloqueado mesmo com a senha certa
    }

    [Fact]
    public async Task Refresh_token_rotation_detects_reuse_and_kills_every_session()
    {
        var company = await stack.CreateCompanyAsync();
        var login = await stack.LoginRawAsync(company.Admin.Email);
        var original = login.GetProperty("refreshToken").GetString();

        var rotated = await stack.Identity.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var newRefresh = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString();
        Assert.NotEqual(original, newRefresh);

        // Reapresentar o token antigo = possível roubo.
        var reuse = await stack.Identity.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // Consequência: o token novo também deixa de valer.
        var afterReuse = await stack.Identity.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = newRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Logout_invalidates_the_refresh_token()
    {
        var company = await stack.CreateCompanyAsync();
        var refresh = (await stack.LoginRawAsync(company.Admin.Email)).GetProperty("refreshToken").GetString();

        await stack.Identity.PostAsJsonAsync("/api/auth/logout", new { refreshToken = refresh });

        var response = await stack.Identity.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Responses_never_leak_internals_on_bad_input()
    {
        var response = await stack.Tickets.SendAsync(
            (await stack.CreateCompanyAsync()).Admin.Token, HttpMethod.Post, "/api/tickets",
            new { title = "x", description = "", priority = "NaoExiste" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("   at ", body); // stack trace
    }
}
