using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Checks that every test host in this assembly applies <see cref="TestHostIsolation"/>.
///
/// <para>A factory that skips it runs the API's background workers against its shared SQLite
/// connection again. When that goes wrong, an unrelated test fails now and then, not the factory
/// that caused it. This test fails every time instead, so the cause is easy to find.</para>
///
/// <para>It builds every factory that derives directly from <see cref="WebApplicationFactory{TEntryPoint}"/>,
/// including ones added later. Subclasses such as the ones based on <see cref="TestFactory"/> get
/// the same isolation from their base, so they are not built again here.</para>
/// </summary>
public class TestHostIsolationTests
{
    public static TheoryData<Type> RootFactories()
    {
        var data = new TheoryData<Type>();
        var roots = typeof(TestHostIsolationTests).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.BaseType == typeof(WebApplicationFactory<Program>))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var type in roots) data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(RootFactories))]
    public async Task Host_RunsNoneOfTheApisBackgroundWorkers(Type factoryType)
    {
        await using var factory = (WebApplicationFactory<Program>)Activator.CreateInstance(factoryType)!;

        var apiWorkers = factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Assembly == typeof(Program).Assembly)
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(apiWorkers);
    }
}
