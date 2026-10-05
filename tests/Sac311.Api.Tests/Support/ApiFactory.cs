using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sac311.Data;
using Sac311.Data.Aggregates;
using Sac311.Domain.Aggregates;

namespace Sac311.Api.Tests.Support;

/// <summary>The API as Program.cs builds it, on a test database and a clock fixed at <paramref name="nowUtc"/>.</summary>
public sealed class ApiFactory(string connectionString, DateTime nowUtc) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sac311"] = connectionString,
        }));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedTime(nowUtc)));
    }

    /// <summary>Classifies, computes and publishes the aggregates over what is in the database, as the worker does after a run.</summary>
    public static async Task RefreshAggregatesAsync(SqlServerFixture db, DateTime asOfUtc)
    {
        var store = new AggregateStore(new Sac311Db(db.ConnectionString));
        await store.ClassifyAsync(CancellationToken.None);
        var builder = new AggregateBuilder(asOfUtc);
        await foreach (var request in store.ReadRequestsAsync(CancellationToken.None))
        {
            builder.Add(request);
        }

        await store.PublishAsync(builder.Build(), null, 0, CancellationToken.None);
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
