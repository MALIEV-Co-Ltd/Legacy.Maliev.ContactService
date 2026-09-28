using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.ContactService.Tests.Authentication;

/// <summary>Production JWT acceptance checks for this service's shared authentication boundary.</summary>
public sealed class ProductionJwtAuthenticationTests
{
    private const string Issuer = "https://api.maliev.com";
    private const string Audience = "https://api.maliev.com";
    private const string LegacyHmacKey = "test-key-at-least-32-characters-long"; // gitleaks:allow

    [Fact]
    public void Program_UsesSharedAsymmetricAuthentication()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Legacy.Maliev.ContactService.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var program = File.ReadAllText(Path.Combine(root.FullName, "Legacy.Maliev.ContactService.Api", "Program.cs"));
        Assert.Contains("builder.AddJwtAuthentication();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddJwtAuthenticationSymmetric", program, StringComparison.Ordinal);
        Assert.Contains("app.UseAuthentication();", program, StringComparison.Ordinal);
        Assert.Contains("app.UseAuthorization();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_AcceptsExternalRs256_AndRejectsLegacyHmacAndOtherRsaAlgorithms()
    {
        using var trustedRsa = RSA.Create(2048);
        var parameters = ConfigureProduction(trustedRsa);
        var handler = new JwtSecurityTokenHandler();

        var accepted = CreateToken(handler, new RsaSecurityKey(trustedRsa), SecurityAlgorithms.RsaSha256, Issuer, Audience);
        handler.ValidateToken(accepted, parameters, out _);

        var symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LegacyHmacKey));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, symmetricKey, SecurityAlgorithms.HmacSha256, Issuer, Audience), parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, new RsaSecurityKey(trustedRsa), SecurityAlgorithms.RsaSha384, Issuer, Audience), parameters, out _));
    }

    [Fact]
    public void Production_RejectsWrongIssuerAudienceAndSigningKey()
    {
        using var trustedRsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var parameters = ConfigureProduction(trustedRsa);
        var handler = new JwtSecurityTokenHandler();
        var trustedKey = new RsaSecurityKey(trustedRsa);

        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, trustedKey, SecurityAlgorithms.RsaSha256, "https://other.example", Audience), parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, trustedKey, SecurityAlgorithms.RsaSha256, Issuer, "other-audience"), parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, new RsaSecurityKey(otherRsa), SecurityAlgorithms.RsaSha256, Issuer, Audience), parameters, out _));
    }

    [Theory]
    [InlineData("Jwt:PublicKey")]
    [InlineData("Jwt:Issuer")]
    [InlineData("Jwt:Audience")]
    public void Production_MissingExternalKeyOrTrustBoundary_FailsClosed(string missingSetting)
    {
        using var rsa = RSA.Create(2048);
        var builder = CreateProductionBuilder(rsa);
        builder.Configuration[missingSetting] = null;

        Assert.Throws<InvalidOperationException>(() => builder.AddJwtAuthentication());
    }

    private static TokenValidationParameters ConfigureProduction(RSA rsa)
    {
        var builder = CreateProductionBuilder(rsa);
        builder.AddJwtAuthentication();

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.Equal([SecurityAlgorithms.RsaSha256], options.TokenValidationParameters.ValidAlgorithms);
        Assert.IsType<RsaSecurityKey>(options.TokenValidationParameters.IssuerSigningKey);
        Assert.Null(options.TokenValidationParameters.IssuerSigningKeys);
        return options.TokenValidationParameters;
    }

    private static HostApplicationBuilder CreateProductionBuilder(RSA rsa)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:SecurityKey"] = LegacyHmacKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });
        return builder;
    }

    private static string CreateToken(
        JwtSecurityTokenHandler handler,
        SecurityKey signingKey,
        string algorithm,
        string issuer,
        string audience)
    {
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(signingKey, algorithm));
        return handler.WriteToken(token);
    }
}
