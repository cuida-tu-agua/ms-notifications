using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SyWater.Notifications.Api.Security;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Infrastructure.Persistence;

namespace SyWater.Notifications.Api.Tests;

public sealed class NoRevocations : ITokenRevocationChecker
{
    public Task<bool> IsRevokedAsync(string? tokenId, string? userId, long? issuedAt) => Task.FromResult(false);
}

/// <summary>The real API (Program.cs) with SQLite, a test RSA key and a fake Redis revocation list.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://iam.cuidatuagua.local";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly SqliteConnection _sqlite = new("DataSource=:memory:");
    private readonly string _publicKeyPath = Path.Combine(Path.GetTempPath(), $"iam-public-{Guid.NewGuid():N}.pem");

    public FakeContacts Contacts { get; } = new();
    public FakeSender Sender { get; } = new();
    public FakePushSender Push { get; } = new();

    public ApiFactory()
    {
        _sqlite.Open();
        File.WriteAllText(_publicKeyPath, _rsa.ExportSubjectPublicKeyInfoPem());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Notification", "unused-in-tests");
        builder.UseSetting("Jwt:PublicKeyPath", _publicKeyPath);
        builder.UseSetting("RabbitMq:Host", "");   // no consumer: the tests call the handler directly
        builder.UseSetting("Internal:ApiKey", "test-internal-key-0123456789");   // the e-mail channel is on in Development (Mailpit)
        builder.UseSetting("Services:PlacesBaseUrl", "");   // no ms-places here: only the owner hears about the alerts (HU-047 has its own tests)

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<NotificationDbContext>();
            services.RemoveAll<DbContextOptions<NotificationDbContext>>();
            var options = new DbContextOptionsBuilder<NotificationDbContext>().UseSqlite(_sqlite).Options;
            services.AddScoped<NotificationDbContext>(_ => new NotificationDbContext(options));

            services.RemoveAll<IHostedService>();   // no RabbitMQ consumer, no e-mail worker: the tests call the use cases
            services.RemoveAll<IUserContactDirectory>();
            services.AddSingleton<IUserContactDirectory>(Contacts);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Sender);
            services.RemoveAll<IPushSender>();   // never talk to the real Expo
            services.AddSingleton<IPushSender>(Push);

            services.RemoveAll<ITokenRevocationChecker>();
            services.AddSingleton<ITokenRevocationChecker, NoRevocations>();
        });
    }

    public void CreateSchema()
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<NotificationDbContext>().Database.EnsureCreated();
    }

    /// <summary>Stores a notification the way the event consumer will (through the repository port).</summary>
    public async Task SeedAsync(Audience audience, string title = "Title", NotificationSeverity severity = NotificationSeverity.Info)
    {
        using var scope = Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
        await repo.AddAsync(Notification.Create(audience, NotificationType.ValveChanged, severity, title, "Body",
            null, null, DateTime.UtcNow), default);
    }

    public string TokenFor(Guid userId, params string[] roles) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["jti"] = Guid.NewGuid().ToString(),
                ["roles"] = roles,
            },
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "iam-key-1" }, SecurityAlgorithms.RsaSha256),
        });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _sqlite.Dispose();
        File.Delete(_publicKeyPath);
    }
}
