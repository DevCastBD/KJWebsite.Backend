using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FinanceService.Payments;

/// <summary>Picks which adapters to try for a donation. It knows nothing about any one provider: it works from the registered <see cref="IPaymentGateway"/> set.</summary>
public sealed class PaymentGatewayRegistry(IEnumerable<IPaymentGateway> gateways, IOptions<PaymentOptions> options)
{
    private readonly IReadOnlyList<IPaymentGateway> _gateways = [.. gateways];

    /// <summary>Every registered adapter, switched on or not.</summary>
    public IReadOnlyList<string> KnownIds => [.. _gateways.Select(g => g.Id)];

    public IReadOnlyList<string> EnabledIds => [.. _gateways.Where(g => g.IsEnabled).Select(g => g.Id)];

    /// <summary>
    /// A donor who named a gateway gets only that one (and nothing if it is off or cannot take the currency, so the donation falls back to
    /// the offline path rather than silently paying elsewhere). Otherwise every enabled adapter that supports the currency, in configured order.
    /// </summary>
    public IReadOnlyList<IPaymentGateway> Candidates(string? requested, string currency)
    {
        var usable = _gateways.Where(g => g.IsEnabled && g.Supports(currency)).ToList();
        if (!string.IsNullOrEmpty(requested))
        {
            return [.. usable.Where(g => string.Equals(g.Id, requested, StringComparison.Ordinal)).Take(1)];
        }

        var order = options.Value.EffectiveOrder.ToList();
        return [.. usable.OrderBy(g => order.IndexOf(g.Id) is var i and >= 0 ? i : int.MaxValue).ThenBy(g => g.Id, StringComparer.Ordinal)];
    }
}

public static class PaymentGatewayRegistration
{
    /// <summary>Registers an adapter and the named, log-free HTTP client it uses.</summary>
    public static IServiceCollection AddPaymentGateway<TGateway>(this IServiceCollection services, string httpClientName) where TGateway : class, IPaymentGateway
    {
        services.AddHttpClient(httpClientName).RemoveAllLoggers();
        services.TryAddSingleton<TGateway>();
        services.AddSingleton<IPaymentGateway>(provider => provider.GetRequiredService<TGateway>());
        return services;
    }
}
