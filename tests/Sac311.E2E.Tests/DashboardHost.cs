using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Playwright;
using Sac311.Api.Tests.Support;

namespace Sac311.E2E.Tests;

/// <summary>
/// The whole dashboard for a test class: a throwaway database seeded with the API tests' scenario
/// (<see cref="SeededApi"/>), the API on Kestrel with its clock 10 minutes after the as-of time, the built Angular app
/// (web/dist) served from the same origin as the API (as the dev proxy does), and headless Chromium.
/// </summary>
public sealed class DashboardHost : IAsyncLifetime, IDisposable
{
    private DashboardFactory? _factory;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public SqlServerFixture Db { get; } = new();

    /// <summary>The dashboard's address, e.g. <c>http://127.0.0.1:52345/</c>.</summary>
    public Uri BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dist = FindDist();
        InstallChromium();

        await Db.InitializeAsync();
        await SeededApi.SeedAsync(Db);

        _factory = new DashboardFactory(Db.ConnectionString, SeededApi.AsOfUtc.AddMinutes(10), dist);
        _factory.UseKestrel(0);
        _factory.StartServer();
        var address = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUrl = new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/') + "/");

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    /// <summary>A fresh page (its own browser context) at <paramref name="path"/>, e.g. <c>?window=30</c>.</summary>
    public async Task<IPage> OpenAsync(string path = "")
    {
        var page = await NewPageAsync();
        await page.GotoAsync(new Uri(BaseUrl, path).ToString());
        return page;
    }

    /// <summary>A blank page in its own browser context, for a test that has to watch the first navigation.</summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await _browser!.NewContextAsync(new() { ViewportSize = new() { Width = 1400, Height = 1000 } });
        return await context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Db.DisposeAsync();
    }

    public void Dispose() => _factory?.Dispose();

    /// <summary>The Angular build output, found by walking up from the test binaries to the repository's web/ folder.</summary>
    private static string FindDist()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var web = Path.Combine(dir.FullName, "web");
            if (Directory.Exists(web))
            {
                var browser = Path.Combine(web, "dist", "rivercity-pulse", "browser");
                return File.Exists(Path.Combine(browser, "index.html"))
                    ? browser
                    : throw new InvalidOperationException($"No Angular build at {browser}. Run 'npm ci' and 'npm run build' in web/ first.");
            }
        }

        throw new InvalidOperationException("Can't find the repository's web/ folder above " + AppContext.BaseDirectory);
    }

    /// <summary>Downloads Chromium for this Playwright version if it isn't there yet (a no-op after the first time).</summary>
    private static void InstallChromium()
    {
        // On a CI runner also install the system libraries Chromium needs.
        string[] args = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("CI") is not null
            ? ["install", "--with-deps", "chromium"]
            : ["install", "chromium"];
        var exit = Microsoft.Playwright.Program.Main(args);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Playwright couldn't install Chromium (exit {exit}).");
        }
    }

    private sealed class DashboardFactory(string connectionString, DateTime nowUtc, string dist) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sac311"] = connectionString,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FixedTime(nowUtc));
                services.AddSingleton<IStartupFilter>(new StaticDashboard(dist));
            });
        }
    }

    /// <summary>Serves the built app ahead of the API's own pipeline, so <c>/</c> is the dashboard rather than the Swagger redirect.</summary>
    private sealed class StaticDashboard(string root) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var files = new PhysicalFileProvider(root);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            next(app);
        };
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
