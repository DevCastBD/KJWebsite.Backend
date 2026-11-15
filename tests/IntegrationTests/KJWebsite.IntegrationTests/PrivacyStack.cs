using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data.Entities;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

#pragma warning disable CA1711 // The fixture name is the shared test API.
#pragma warning disable CA1822 // Keep fixture helpers available through the fixture instance.
public sealed class PrivacyStack : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private readonly List<IDisposable> _handlers = [];
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-privacy-proofs-{Guid.NewGuid():N}");
    public AuthApiFactory Auth { get; } = new();
    public ContentApiFactory Content { get; } = new();
    public CtaApiFactory Cta { get; } = new();
    public MembershipApiFactory Membership { get; } = new();
    public FinanceApiFactory Finance { get; } = new();
    public SponsorshipApiFactory Sponsorship { get; } = new();
    public GatewayApiFactory GatewayFactory { get; } = new();
    public HttpClient Gateway { get; private set; } = null!;
    public Task InitializeAsync()
    {
        Membership.AuthHandler = Lazy(() => Auth.Server.CreateHandler());
        Finance.AuthHandler = Lazy(() => Auth.Server.CreateHandler());
        Finance.SponsorshipHandler = Lazy(() => Sponsorship.Server.CreateHandler());
        Finance.Settings["Finance:Proofs:Path"] = _proofs;
        Sponsorship.FinanceHandler = Lazy(() => Finance.Server.CreateHandler());
        GatewayFactory.ClusterHandlers = new()
        {
            ["auth"] = () => Count("auth", () => Auth.Server.CreateHandler()),
            ["content"] = () => Count("content", () => Content.Server.CreateHandler()),
            ["cta"] = () => Count("cta", () => Cta.Server.CreateHandler()),
            ["membership"] = () => Count("membership", () => Membership.Server.CreateHandler()),
            ["finance"] = () => Count("finance", () => Finance.Server.CreateHandler()),
            ["sponsorship"] = () => Count("sponsorship", () => Sponsorship.Server.CreateHandler())
        };
        // Start all six hosts once. Handlers still resolve the receiving server only on first use.
        _ = Auth.Server; _ = Content.Server; _ = Cta.Server; _ = Membership.Server; _ = Finance.Server; _ = Sponsorship.Server;
        Gateway = GatewayFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        return Task.CompletedTask;
    }
    private LazyHandler Lazy(Func<HttpMessageHandler> create)
    {
        var handler = new LazyHandler(create);
        _handlers.Add(handler);
        return handler;
    }
    private CountingHandler Count(string cluster, Func<HttpMessageHandler> create)
    {
        var handler = new CountingHandler(() => _calls.AddOrUpdate(cluster, 1, (_, count) => count + 1), Lazy(create));
        _handlers.Add(handler);
        return handler;
    }
    public int Calls(string cluster) => _calls.GetValueOrDefault(cluster);
    public void ResetCalls() => _calls.Clear();
    public string StaffToken(params string[] permissions) => TestTokens.Issue(permissions);
    public string Seed(string label, string value) { GlobalLogSink.Default.Register(label, value); return value; }
    public string NewSeed(string prefix) => Seed(prefix, $"KJPRIV-{prefix}-{Guid.NewGuid():N}");
    public async Task<string> LoginAsync(string email, string password)
    {
        using var response = await Gateway.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }
    public Task<(string UserId, string Email, string Password, string Token)> NewAdminAsync() => NewUserAsync(KjRoles.Admin);
    public Task<(string UserId, string Email, string Password, string Token)> NewSuperAdminAsync() => NewUserAsync(KjRoles.SuperAdmin);
    private async Task<(string UserId, string Email, string Password, string Token)> NewUserAsync(string role)
    {
        var email = Seed("staff email", $"{NewSeed("staff")}@example.org");
        var password = NewSeed("Password9");
        using var scope = Auth.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = new AuthUserEntity { UserName = email, Email = email, EmailConfirmed = true, Status = "active", CreatedAt = DateTimeOffset.UtcNow };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded) throw new InvalidOperationException(string.Join(", ", created.Errors.Select(e => e.Description)));
        var assigned = await users.AddToRoleAsync(user, role);
        if (!assigned.Succeeded) throw new InvalidOperationException("Could not assign staff role.");
        return (user.Id, email, password, await LoginAsync(email, password));
    }
    public Task DisposeAsync()
    {
        var failures = new List<Exception>();
        void Clean(Action action) { try { action(); } catch (Exception exception) { failures.Add(exception); } }
        Clean(() => Gateway?.Dispose());
        foreach (var factory in new IDisposable[] { GatewayFactory, Sponsorship, Finance, Membership, Cta, Content, Auth }) Clean(factory.Dispose);
        foreach (var handler in _handlers) Clean(handler.Dispose);
        Clean(() => { if (Directory.Exists(_proofs)) Directory.Delete(_proofs, true); });
        if (failures.Count != 0) throw new AggregateException(failures);
        return Task.CompletedTask;
    }
    private sealed class LazyHandler(Func<HttpMessageHandler> create) : HttpMessageHandler
    {
        private readonly Lazy<HttpMessageInvoker> _invoker = new(() => new(create()));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _invoker.Value.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing && _invoker.IsValueCreated) _invoker.Value.Dispose(); base.Dispose(disposing); }
    }
    private sealed class CountingHandler(Action count, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { count(); return base.SendAsync(request, cancellationToken); }
    }
}
