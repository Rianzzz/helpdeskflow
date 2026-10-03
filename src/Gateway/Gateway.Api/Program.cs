using HelpDeskFlow.Observability;
using System.Threading.RateLimiting;
using HelpDeskFlow.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados + traces + métricas (building block compartilhado).
builder.AddHelpDeskObservability("gateway");

// ---- Kestrel: limites básicos de proteção ----
builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;                     // não anuncia o servidor/versão
    o.Limits.MaxRequestBodySize = 1_000_000;       // 1 MB: corpo maior que isso é rejeitado (413)
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});

// ---- Autenticação: o gateway valida o JWT ANTES de encaminhar ----
// Os serviços continuam validando também (defesa em profundidade): se alguém acessar um serviço
// direto, sem passar pelo gateway, ele continua protegido.
builder.Services.AddHelpDeskAuthentication(builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    // Postura segura por padrão: toda rota exige login, a menos que seja marcada "anonymous" no YARP.
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ---- Rate limiting ----
var tenantLimit = builder.Configuration.GetValue("RateLimiting:TenantPermitPerMinute", 100);
var authLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 10);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Rotas de login/cadastro: por IP (ainda não sabemos quem é o usuário).
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        $"ip:{http.Connection.RemoteIpAddress}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // Rotas autenticadas: uma "cota" POR EMPRESA. Um cliente barulhento não derruba as outras empresas
    // (o problema do "vizinho barulhento" em SaaS multi-tenant).
    o.AddPolicy("tenant", http =>
    {
        var tenantId = http.User.FindFirst(AppClaims.TenantId)?.Value;
        var key = tenantId is null ? $"ip:{http.Connection.RemoteIpAddress}" : $"tenant:{tenantId}";
        return RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = tenantLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });

    o.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { title = "Muitas requisições", status = 429, detail = "Limite excedido. Tente novamente em instantes." }, ct);
    };
});

// ---- CORS: só origens explicitamente permitidas (front-ends conhecidos) ----
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("web", p =>
{
    if (allowedOrigins.Length > 0)
        p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

// ---- Proxy reverso (rotas e destinos vêm do appsettings.json) ----
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
app.UseHelpDeskObservability(); // correlation id + log de requisições

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Erros inesperados nunca vazam detalhes.
app.UseExceptionHandler(h => h.Run(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { title = "Erro interno", status = 500 });
}));

// ---- Cabeçalhos de segurança (o X-Correlation-Id é gerado pelo middleware de observabilidade) ----
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var h = context.Response.Headers;
        h.Remove("Server");                       // o proxy copiaria o "Server: Kestrel" dos serviços internos
        h.Remove("X-Powered-By");
        // O proxy copia o X-Correlation-Id da resposta do serviço; aqui garantimos um valor só, o desta requisição.
        h[ObservabilityExtensions.CorrelationHeader] = context.Request.Headers[ObservabilityExtensions.CorrelationHeader].ToString();
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'"; // API não serve páginas
        h["Cache-Control"] = "no-store";                                              // respostas autenticadas não devem ser cacheadas
        return Task.CompletedTask;
    });

    await next();
});

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();   // depois da autenticação: assim já sabemos o tenant do usuário
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapHelpDeskHealth();
app.MapReverseProxy();

app.Run();
