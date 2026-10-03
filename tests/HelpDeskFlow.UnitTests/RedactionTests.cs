using HelpDeskFlow.Observability;

namespace HelpDeskFlow.UnitTests;

public class UrlRedactionTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.assinatura";

    [Theory]
    [InlineData($"?id=abc&access_token={Jwt}", "?id=abc&access_token=[REDACTED]")]
    [InlineData($"?access_token={Jwt}&id=abc", "?access_token=[REDACTED]&id=abc")]
    [InlineData($"http://localhost:5082/hubs/notifications?id=1&access_token={Jwt}", "http://localhost:5082/hubs/notifications?id=1&access_token=[REDACTED]")]
    [InlineData($"?ACCESS_TOKEN={Jwt}", "?ACCESS_TOKEN=[REDACTED]")]
    public void The_token_never_survives_redaction(string input, string expected)
    {
        var redacted = UrlRedaction.RedactSensitiveQuery(input);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("eyJ", redacted);
    }

    [Fact]
    public void Urls_without_a_token_are_left_alone()
    {
        const string url = "http://localhost/api/tickets?status=Open&page=2";

        Assert.Equal(url, UrlRedaction.RedactSensitiveQuery(url));
    }
}
