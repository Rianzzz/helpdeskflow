using System.Diagnostics;
using System.Text.Json;
using HelpDeskFlow.Messaging.RabbitMq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace HelpDeskFlow.Observability;

public static class ObservabilityExtensions
{
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>Fonte de atividades (spans) própria do HelpDeskFlow, para trechos como o dispatcher do Outbox.</summary>
    public const string ActivitySourceName = "HelpDeskFlow";

    /// <summary>
    /// Liga os "três pilares" da observabilidade neste serviço:
    ///  • LOGS estruturados (Serilog), com o nome do serviço, TraceId e CorrelationId em cada linha;
    ///  • TRACES distribuídos (OpenTelemetry): uma requisição vira uma árvore de spans que atravessa
    ///    gateway → serviços → banco → RabbitMQ → outros serviços;
    ///  • MÉTRICAS (requisições, runtime .NET).
    /// Os traces e métricas só são exportados se OTEL_EXPORTER_OTLP_ENDPOINT estiver configurado.
    /// </summary>
    public static WebApplicationBuilder AddHelpDeskObservability(this WebApplicationBuilder builder, string serviceName)
    {
        var isDevelopment = builder.Environment.IsDevelopment();

        builder.Host.UseSerilog((context, services, logger) =>
        {
            logger
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                .MinimumLevel.Override("Yarp", LogEventLevel.Warning)
                .ReadFrom.Configuration(context.Configuration) // permite ajustar níveis pelo appsettings ("Serilog")
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", serviceName)
                .Enrich.With<TraceContextEnricher>();

            if (isDevelopment)
                logger.WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] {Message:lj} {Properties:j}{NewLine}{Exception}");
            else
                // Em produção: JSON, uma linha por evento, pronto para Loki/Elastic/CloudWatch.
                logger.WriteTo.Console(new RenderedCompactJsonFormatter());
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                    // O SignalR manda o JWT em "?access_token=...": não pode ir parar no sistema de traces.
                    o.EnrichWithHttpRequest = (activity, request) =>
                    {
                        if (request.QueryString.HasValue)
                            activity.SetTag("url.query", UrlRedaction.RedactSensitiveQuery(request.QueryString.Value!));
                    };
                })
                .AddHttpClientInstrumentation(o =>
                    // O gateway repassa a query string ao serviço: mesma proteção nas chamadas de saída.
                    o.EnrichWithHttpRequestMessage = (activity, request) =>
                    {
                        if (request.RequestUri is { Query.Length: > 0 } uri)
                            activity.SetTag("url.full", UrlRedaction.RedactSensitiveQuery(uri.ToString()));
                    })
                .AddNpgsql()
                .AddSource(ActivitySourceName)
                .AddSource("RabbitMQ.Client.Publisher", "RabbitMQ.Client.Subscriber"))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        // Exportador OTLP só quando há um coletor configurado (Jaeger, Grafana Tempo, Datadog...).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder.Services.AddOpenTelemetry().UseOtlpExporter();

        builder.Services.AddHealthChecks();
        return builder;
    }

    /// <summary>Middlewares de observabilidade. Chame logo após o Build(), antes de todo o resto.</summary>
    public static WebApplication UseHelpDeskObservability(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();

        // Uma linha por requisição ("GET /api/tickets respondeu 200 em 12 ms") em vez de várias linhas soltas.
        app.UseSerilogRequestLogging(o =>
        {
            o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
                : ctx.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        });
        return app;
    }

    /// <summary>
    /// /health/live  → "o processo está de pé" (o orquestrador reinicia se falhar);
    /// /health/ready → "consigo atender?": banco e broker respondendo (o orquestrador tira do balanceamento se falhar).
    /// Anônimos de propósito e SEM detalhes de exceção: só dizem saudável ou não.
    /// </summary>
    public static WebApplication MapHelpDeskHealth(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteSummary
        }).AllowAnonymous();
        return app;
    }

    /// <summary>Verifica se o RabbitMQ aceita conexões.</summary>
    public static IHealthChecksBuilder AddRabbitMqCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

    private static Task WriteSummary(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString())
        }));
    }
}

/// <summary>Remove segredos que viajam na URL (hoje: o JWT do SignalR em "access_token") antes de gravar logs e traces.</summary>
public static partial class UrlRedaction
{
    public const string Placeholder = "[REDACTED]";

    [System.Text.RegularExpressions.GeneratedRegex("(access_token=)[^&#]*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AccessToken();

    public static string RedactSensitiveQuery(string urlOrQuery) => AccessToken().Replace(urlOrQuery, "$1" + Placeholder);
}

/// <summary>
/// Garante um X-Correlation-Id em toda requisição. O gateway o cria; os serviços o reaproveitam. Ele vai para o
/// header de resposta (o cliente pode citá-lo ao pedir suporte), para o span atual e para todos os logs da requisição.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[ObservabilityExtensions.CorrelationHeader].FirstOrDefault();
        // Aceita só um GUID válido: um valor livre vindo de fora poderia "forjar" linhas em logs.
        var correlationId = Guid.TryParse(incoming, out var parsed) ? parsed.ToString() : Guid.NewGuid().ToString();

        context.Request.Headers[ObservabilityExtensions.CorrelationHeader] = correlationId; // o proxy repassa adiante
        context.Response.Headers[ObservabilityExtensions.CorrelationHeader] = correlationId;
        Activity.Current?.SetTag("correlation_id", correlationId);

        using (LogContext.PushProperty("CorrelationId", correlationId))
            await next(context);
    }
}

/// <summary>Põe o TraceId/SpanId do OpenTelemetry em cada linha de log: a ponte entre logs e traces.</summary>
public sealed class TraceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        var activity = Activity.Current;
        if (activity is null) return;

        logEvent.AddPropertyIfAbsent(factory.CreateProperty("TraceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("SpanId", activity.SpanId.ToString()));
    }
}

public sealed class RabbitMqHealthCheck(RabbitConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var conn = await connection.GetAsync(timeout.Token);
            return conn.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("conexão fechada");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ indisponível");
        }
    }
}
