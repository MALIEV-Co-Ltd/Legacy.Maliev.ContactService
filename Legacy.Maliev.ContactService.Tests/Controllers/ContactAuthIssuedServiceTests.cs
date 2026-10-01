using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.ContactService.Api.Authorization;
using Legacy.Maliev.ContactService.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.ContactService.Tests.Controllers;

/// <summary>Actual Auth-issued service tokens through normal Production Contact and disposable PostgreSQL.</summary>
public sealed class ContactAuthIssuedServiceTests(ContactAuthIssuedFixture fixture) : IClassFixture<ContactAuthIssuedFixture>
{
    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task ActualIssuer_CreateGrant_PersistsLegacyWireThroughBothAliases(string route)
    {
        var wire = await fixture.LoginAsync("contact-create");
        Assert.Equal(new[] { "accessToken", "expiresIn", "tokenType" }, wire.EnumerateObject().Select(value => value.Name).Order().ToArray());
        Assert.Equal("Bearer", wire.GetProperty("tokenType").GetString());
        Assert.InRange(wire.GetProperty("expiresIn").GetInt32(), 1, 900);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(wire.GetProperty("accessToken").GetString());
        Assert.Equal(SecurityAlgorithms.RsaSha256, token.Header.Alg);
        Assert.Equal(ContactAuthIssuedFixture.Issuer, token.Issuer);
        Assert.Equal([ContactAuthIssuedFixture.Audience], token.Audiences);
        Assert.Equal("service:contact-create", Assert.Single(token.Claims, claim => claim.Type == "sub").Value);
        Assert.Equal("service", Assert.Single(token.Claims, claim => claim.Type == "identity_kind").Value);
        Assert.Equal([ContactRequestPermissions.ContactRequestsCreate], token.Claims.Where(claim => claim.Type == "permissions").Select(claim => claim.Value));
        using var client = fixture.ContactClient(wire);
        var marker = Guid.NewGuid().ToString("D");
        using var created = await client.PostAsJsonAsync(route, new { FirstName = "ทดสอบ", MessageContent = marker });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.Equal("ทดสอบ", body.GetProperty("firstName").GetString());
        Assert.Equal($"/Messages/{id}", created.Headers.Location?.AbsolutePath);
        Assert.False(body.TryGetProperty("refreshToken", out _));
        Assert.False(body.TryGetProperty("accessToken", out _));
        await using var database = fixture.Database();
        var row = await database.Messages.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(marker, row.MessageContent);
        Assert.Equal("ทดสอบ", row.FirstName);
        // A create-only machine token is not silently promoted to a read grant.
        using var deniedRead = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        var parameters = fixture.Contact.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        Assert.True(parameters.ValidateIssuer && parameters.ValidateAudience && parameters.ValidateIssuerSigningKey && parameters.ValidateLifetime);
        Assert.Null(parameters.SignatureValidator);
        Assert.Equal([SecurityAlgorithms.RsaSha256], parameters.ValidAlgorithms);
    }

    [Fact]
    public async Task ActualIssuer_RenewedMachineTokenHasFreshJti_NotRefreshState()
    {
        var first = await fixture.LoginAsync("contact-create");
        var second = await fixture.LoginAsync("contact-create");
        var reader = new JwtSecurityTokenHandler();
        var firstJwt = reader.ReadJwtToken(first.GetProperty("accessToken").GetString());
        var secondJwt = reader.ReadJwtToken(second.GetProperty("accessToken").GetString());
        Assert.NotEqual(firstJwt.Id, secondJwt.Id);
        Assert.Equal(firstJwt.Subject, secondJwt.Subject);
        Assert.False(first.TryGetProperty("refreshToken", out _));
        Assert.False(second.TryGetProperty("refreshToken", out _));
        await using var database = fixture.Database();
        Assert.False(await database.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = 'public' AND tablename = 'refresh_sessions') AS \"Value\"").SingleAsync());
    }

    [Theory]
    [InlineData("/Messages")]
    [InlineData("/messages/v1/contact-requests")]
    public async Task ActualIssuer_ReadOnlyGrant_CannotCreateContact(string route)
    {
        using var client = fixture.ContactClient(await fixture.LoginAsync("contact-read"));
        var marker = Guid.NewGuid().ToString("D");
        using var denied = await client.PostAsJsonAsync(route, new { MessageContent = marker });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var database = fixture.Database();
        Assert.False(await database.Messages.AnyAsync(value => value.MessageContent == marker));
    }

    [Theory]
    [InlineData("contact-create")]
    [InlineData("missing-client")]
    public async Task ActualIssuer_InvalidCredential_ReturnsGeneric401WithoutToken(string client)
    {
        using var response = await fixture.LoginResponseAsync(client, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(wire.RootElement.TryGetProperty("accessToken", out _));
        Assert.False(wire.RootElement.TryGetProperty("refreshToken", out _));
    }
}

public sealed class ContactAuthIssuedFixture : IAsyncLifetime
{
    public const string Issuer = "https://contact-auth-proof.invalid";
    public const string Audience = "contact-auth-proof";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly RSA rsa = RSA.Create(2048);
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private Process? auth;
    private HttpClient? issuer;
    private Timer? deadline;
    private int disposed;
    public WebApplicationFactory<Program> Contact { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            await postgres.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            await using (var database = Database()) await database.Database.MigrateAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.ContactService.slnx"))) directory = directory.Parent;
            var binary = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Proof repository missing."), ".dependencies", "contact-auth-proof", "auth", "Legacy.Maliev.AuthService.Api", "bin", "Release", "net10.0", "Legacy.Maliev.AuthService.Api.dll");
            if (!File.Exists(binary)) throw new InvalidOperationException("Build must prepare the pinned Auth proof.");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(binary)!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var inherited = new Dictionary<string, string?>();
            foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "HOME", "USERPROFILE", "DOTNET_ROOT" }) inherited[name] = Environment.GetEnvironmentVariable(name);
            start.Environment.Clear();
            foreach (var (name, value) in inherited) if (value is not null) start.Environment[name] = value;
            start.ArgumentList.Add(binary);
            start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["Jwt__Issuer"] = Issuer;
            start.Environment["Jwt__Audience"] = Audience;
            start.Environment["Jwt__PrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem();
            start.Environment["Jwt__KeyId"] = "ephemeral-contact-proof";
            start.Environment["CORS__AllowedOrigins__0"] = Issuer;
            foreach (var name in new[] { "CustomerIdentity", "EmployeeIdentity", "RefreshSessions" }) start.Environment[$"ConnectionStrings__{name}"] = postgres.GetConnectionString();
            foreach (var (client, permission) in new[] { ("contact-create", ContactRequestPermissions.ContactRequestsCreate), ("contact-read", ContactRequestPermissions.ContactRequestsRead) })
            {
                start.Environment[$"ServiceClients__Clients__{client}__SecretSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
                start.Environment[$"ServiceClients__Clients__{client}__Permissions__0"] = permission;
            }
            start.Environment["Logging__LogLevel__Default"] = "Warning";
            start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            start.Environment["Logging__Console__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            auth = new Process { StartInfo = start, EnableRaisingEvents = true };
            auth.OutputDataReceived += (_, args) =>
            {
                const string marker = "Now listening on: ";
                if (args.Data is not { } line) return;
                try
                {
                    if (line.StartsWith('{'))
                    {
                        using var json = JsonDocument.Parse(line);
                        if (json.RootElement.TryGetProperty("Message", out var message)) line = message.GetString() ?? "";
                    }
                    var index = line.IndexOf(marker, StringComparison.Ordinal);
                    if (index >= 0) listening.TrySetResult(line[(index + marker.Length)..].Trim());
                }
                catch (JsonException) { listening.TrySetException(new InvalidOperationException("Invalid proof startup metadata.")); }
            };
            // Drain output without printing credentials, tokens or production row data.
            auth.ErrorDataReceived += (_, _) => { };
            auth.Exited += (_, _) => listening.TrySetException(new InvalidOperationException("Pinned issuer exited before readiness."));
            if (!auth.Start()) throw new InvalidOperationException("Pinned issuer did not start.");
            deadline = new Timer(_ =>
            {
                try { if (!auth.HasExited) auth.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
            auth.BeginOutputReadLine();
            auth.BeginErrorReadLine();
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(45));
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1") throw new InvalidOperationException("Proof issuer must bind loopback only.");
            issuer = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
            Contact = new Factory(postgres.GetConnectionString(), rsa.ExportSubjectPublicKeyInfoPem());
        }
        catch { await DisposeAsync(); throw; }
    }

    public ContactRequestDbContext Database() => new(new DbContextOptionsBuilder<ContactRequestDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
    public Task<HttpResponseMessage> LoginResponseAsync(string client, string suppliedSecret) => issuer!.PostAsJsonAsync("/auth/v1/service/login", new { ClientId = client, ClientSecret = suppliedSecret });
    public async Task<JsonElement> LoginAsync(string client)
    {
        using var response = await LoginResponseAsync(client, secret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }
    public HttpClient ContactClient(JsonElement wire)
    {
        var client = Contact.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", wire.GetProperty("accessToken").GetString());
        return client;
    }
    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (deadline is not null) await deadline.DisposeAsync();
        if (Contact is not null) await Contact.DisposeAsync();
        issuer?.Dispose();
        if (auth is not null)
        {
            if (!auth.HasExited) { auth.Kill(entireProcessTree: true); await auth.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            auth.Dispose();
        }
        rsa.Dispose();
        await postgres.DisposeAsync();
    }
    private sealed class Factory(string connection, string publicKey) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:ContactRequestDbContext", connection);
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey)));
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Audience);
        }
    }
}
