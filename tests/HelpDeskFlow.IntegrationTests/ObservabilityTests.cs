using System.Net;

namespace HelpDeskFlow.IntegrationTests;

[Collection(StackCollection.Name)]
public class ObservabilityTests(StackFixture stack)
{
    private HttpClient[] Services => [stack.Identity, stack.Tickets, stack.Tenants, stack.Notifications];

    [Fact]
    public async Task Health_endpoints_are_public_and_report_dependencies_as_healthy()
    {
        foreach (var client in Services)
        {
            var live = await client.GetAsync("/health/live");
            var ready = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            var body = await ready.Content.ReadAsStringAsync();
            Assert.Contains("\"postgres\":\"Healthy\"", body);
            Assert.Contains("\"rabbitmq\":\"Healthy\"", body);
        }
    }

    [Fact]
    public async Task Health_endpoints_do_not_leak_exception_details()
    {
        var body = await (await stack.Tickets.GetAsync("/health/ready")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("Host=", body); // nada de connection string
    }

    [Fact]
    public async Task A_valid_correlation_id_is_echoed_back()
    {
        var id = Guid.NewGuid().ToString();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tickets");
        request.Headers.Add("X-Correlation-Id", id);

        var response = await stack.Tickets.SendAsync(request);

        Assert.Equal(id, response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task An_invalid_correlation_id_is_replaced_to_prevent_log_forging()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tickets");
        request.Headers.Add("X-Correlation-Id", "fake\" level=ERROR msg=\"admin logged in");

        var response = await stack.Tickets.SendAsync(request);

        var returned = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.True(Guid.TryParse(returned, out _));
    }

    [Fact]
    public async Task Every_response_gets_a_correlation_id_even_when_the_client_sends_none()
    {
        var response = await stack.Identity.GetAsync("/health/live");

        Assert.True(Guid.TryParse(response.Headers.GetValues("X-Correlation-Id").Single(), out _));
    }
}
