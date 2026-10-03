using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace HelpDeskFlow.Auth;

public static class AuthExtensions
{
    /// <summary>
    /// Configura a validação de JWT. Todo serviço que recebe requisições autenticadas chama isso,
    /// então a regra de validação fica em um único lugar.
    /// </summary>
    public static IServiceCollection AddHelpDeskAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        // Falha cedo: melhor o serviço nem subir do que rodar com uma chave fraca.
        if (string.IsNullOrWhiteSpace(jwt.SigningKey) || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
            throw new InvalidOperationException(
                "Jwt:SigningKey ausente ou curta demais (mínimo 32 bytes). Configure via variável de ambiente Jwt__SigningKey.");

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                // Mantém os nomes de claim exatamente como estão no token ("sub", "role"...).
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    // Só aceita o algoritmo esperado (bloqueia o ataque clássico de "alg: none").
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AppClaims.Subject,
                    RoleClaimType = AppClaims.Role
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Staff, p => p.RequireRole(Roles.Admin, Roles.Agent))
            .AddPolicy(Policies.AdminOnly, p => p.RequireRole(Roles.Admin));

        return services;
    }
}
