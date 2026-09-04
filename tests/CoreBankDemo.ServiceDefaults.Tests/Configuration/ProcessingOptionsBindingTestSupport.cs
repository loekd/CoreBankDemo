using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoreBankDemo.ServiceDefaults.Tests.Configuration;

/// <summary>
/// Shared binding helper for the processing-options I/O matrix tests. Wires a
/// real <see cref="ServiceCollection"/> against an <see cref="IConfiguration"/>
/// built from an in-memory dictionary and the exact production pipeline
/// (<c>AddOptions&lt;T&gt;().BindConfiguration(...).ValidateDataAnnotations()
/// .ValidateOnStart()</c>) — no mocking of DataAnnotations. Resolving
/// <see cref="IOptions{TOptions}"/>.Value triggers the same
/// <see cref="System.ComponentModel.DataAnnotations"/> validators that
/// <c>ValidateOnStart()</c> would run eagerly against a real host, so this is
/// equivalent to what happens at process startup without needing an
/// <c>IHost</c>.
/// </summary>
internal static class ProcessingOptionsBindingTestSupport
{
    public static T Bind<T>(string sectionName, Dictionary<string, string?> values)
        where T : class
    {
        var prefixed = values.ToDictionary(kv => $"{sectionName}:{kv.Key}", kv => kv.Value);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(prefixed)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<T>()
            .BindConfiguration(sectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value;
    }

    /// <summary>
    /// Binds <typeparamref name="T"/> against an <see cref="IConfiguration"/>
    /// that has zero keys anywhere — not even outside <paramref name="sectionName"/>
    /// — so <c>GetSection(sectionName)</c> resolves to a section that does not
    /// exist in the underlying providers at all, rather than merely lacking one
    /// key within an otherwise-present section (that weaker case is what
    /// <see cref="Bind{T}"/> exercises whenever a caller omits a key from
    /// <c>values</c>, since <c>values</c> always supplies at least one other
    /// key under the same section prefix). Deliberately skips
    /// <c>ValidateDataAnnotations().ValidateOnStart()</c> — a fully-absent
    /// section still must bind every property to its declared default, and
    /// this proves that in isolation from validation.
    /// </summary>
    public static T BindSectionAbsent<T>(string sectionName)
        where T : class
    {
        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<T>()
            .Bind(configuration.GetSection(sectionName));

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value;
    }
}
