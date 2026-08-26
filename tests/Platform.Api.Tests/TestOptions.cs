using Microsoft.Extensions.Options;
using Platform.Api.Infrastructure;

namespace Platform.Api.Tests;

/// <summary>
/// Shared helpers for producing <see cref="IOptionsMonitor{T}"/> instances from plain POCOs
/// in unit tests. DI-heavy services that accept <c>IOptionsMonitor</c> otherwise need a
/// couple of lines of NSubstitute setup in every test constructor.
/// </summary>
internal static class TestOptions
{
    public static IOptionsMonitor<NormalizationOptions> Normalization(NormalizationOptions? value = null)
        => new StaticOptionsMonitor<NormalizationOptions>(value ?? new NormalizationOptions());

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public StaticOptionsMonitor(T value) => _value = value;

        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable OnChange(Action<T, string?> listener) => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
