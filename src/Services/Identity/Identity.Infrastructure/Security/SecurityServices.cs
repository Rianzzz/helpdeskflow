using System.Security.Cryptography;
using System.Text;
using HelpDeskFlow.Auth;
using Identity.Application;
using Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Identity.Infrastructure.Security;

/// <summary>
/// Hash de senha com PBKDF2-HMAC-SHA512 e salt aleatório por senha (implementação da própria Microsoft).
/// O hash embute algoritmo, iterações e salt, então dá para aumentar o custo no futuro sem quebrar logins antigos.
/// </summary>
public class PasswordHasherAdapter : Application.IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();
    private readonly string _dummyHash;

    public PasswordHasherAdapter() => _dummyHash = _hasher.HashPassword(null!, Guid.NewGuid().ToString("N"));

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(null!, hash, password) != PasswordVerificationResult.Failed;

    public void SimulateVerification(string password) =>
        _hasher.VerifyHashedPassword(null!, _dummyHash, password);
}

public class TokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _jwt = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_jwt.RefreshTokenDays);

    public AccessToken CreateAccessToken(User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_jwt.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [AppClaims.Subject] = user.Id.ToString(),
                [AppClaims.TenantId] = user.TenantId.ToString(),
                [AppClaims.Role] = user.Role.ToString(),
                [AppClaims.Email] = user.Email,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)), SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(_handler.CreateToken(descriptor), expires);
    }

    /// <summary>64 bytes aleatórios criptograficamente seguros (não adivinháveis).</summary>
    public string GenerateRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));

    /// <summary>SHA-256 basta aqui: o token já é aleatório e longo, ao contrário de uma senha humana.</summary>
    public string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
}
