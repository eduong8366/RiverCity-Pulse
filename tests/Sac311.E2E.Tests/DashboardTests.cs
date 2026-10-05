using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Sac311.E2E.Tests;

/// <summary>
/// The dashboard in a real browser over the real API and database. Figures come from the <c>SeededApi</c> scenario,
/// whose values are worked out by hand in the API tests: at 30 days Central Oak Park's median is 15.5 against 2 before,
/// Downtown's is 2.5, and the citywide median is 200 (a 100-request Parking clear-out, counted as recorded).
/// </summary>
[Trait("Category", "E2E")]
public partial class DashboardTests(DashboardHost host) : IClassFixture<DashboardHost>
{
    [GeneratedRegex("window=30")]
    private static partial Regex Window30();

    [GeneratedRegex("neighborhood=central-oak-park")]
    private static partial Regex CentralOakParkSelected();

    [GeneratedRegex("category=Streets")]
    private static partial Regex StreetsSelected();

    [Fact]
    public async Task Dashboard_loads_the_map_card_freshness_and_categories()
    {
        var page = await host.OpenAsync("?window=30");

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "RiverCity Pulse" })).ToBeVisibleAsync();
        await Expect(page.Locator("app-freshness-badge .pill")).ToHaveTextAsync("Fresh");
        // All 129 neighborhoods are drawn, each a focusable shape.
        await Expect(page.Locator("path[role=button]")).ToHaveCountAsync(129);
        // Nothing hovered: the card shows the citywide figures.
        var card = page.Locator(".hover-card");
        await Expect(card.Locator("h2")).ToHaveTextAsync("Citywide");
        await Expect(card.Locator(".headline dd")).ToHaveTextAsync("200 days");
        // The categories table: the total, then each service category by requests opened (the clear-out opened none in the window).
        var names = page.Locator("app-categories-table tbody th");
        await Expect(names).ToHaveTextAsync(["All categories", "Solid Waste", "Streets", "Water", "Parking"]);
    }

    [Fact]
    public async Task Slower_panel_follows_the_window_and_opens_the_drawer()
    {
        var page = await host.OpenAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "30 days" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(Window30());

        var panel = page.Locator("app-slower-panel");
        await Expect(panel.Locator(".sentence")).ToContainTextAsync("1 of 2 neighborhoods got slower against the prior 30 days");
        await Expect(panel.Locator("tbody tr")).ToHaveTextAsync([new Regex(@"Central Oak Park\s*15\.5 days\s*2\.0 days\s*\+13\.5 days\s*30\s*–")]);

        await panel.GetByRole(AriaRole.Button, new() { Name = "Open details for Central Oak Park" }).ClickAsync();
        var drawer = page.GetByRole(AriaRole.Dialog);
        await Expect(drawer.Locator("h2")).ToHaveTextAsync("Central Oak Park");
        await Expect(drawer.Locator("h2")).ToBeFocusedAsync();
        await Expect(page).ToHaveURLAsync(CentralOakParkSelected());
        await Expect(drawer.Locator(".compare tbody tr").First).ToHaveTextAsync(new Regex(@"Median days to close\s*15\.5 days\s*2\.0 days"));
        await Expect(drawer.Locator(".trend")).ToHaveTextAsync("675.0% slower than the prior 30 days");

        await drawer.GetByRole(AriaRole.Button, new() { Name = "Close neighborhood details" }).ClickAsync();
        await Expect(drawer).ToHaveCountAsync(0);
        await Expect(page).Not.ToHaveURLAsync(CentralOakParkSelected());
    }

    [Fact]
    public async Task Keyboard_on_the_map_opens_a_neighborhood_and_escape_closes_it()
    {
        var page = await host.OpenAsync("?window=30");
        var downtown = page.Locator("path[aria-label='Downtown']");

        await downtown.FocusAsync();
        // Focus shows the neighborhood in the hover card; Enter opens its drawer.
        await Expect(page.Locator(".hover-card h2")).ToHaveTextAsync("Downtown");
        await downtown.PressAsync("Enter");

        var drawer = page.GetByRole(AriaRole.Dialog);
        await Expect(drawer.Locator("h2")).ToHaveTextAsync("Downtown");
        await Expect(drawer.Locator(".compare tbody tr").First).ToHaveTextAsync(new Regex(@"Median days to close\s*2\.5 days\s*100 days"));
        await Expect(drawer.Locator(".table-wrap tbody th")).ToHaveTextAsync(["Streets"]);

        await page.Keyboard.PressAsync("Escape");
        await Expect(drawer).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Picking_a_category_in_the_table_filters_the_whole_dashboard()
    {
        var page = await host.OpenAsync("?window=30");

        await page.Locator("app-categories-table").GetByRole(AriaRole.Button, new() { Name = "Streets", Exact = true }).ClickAsync();

        await Expect(page).ToHaveURLAsync(StreetsSelected());
        await Expect(page.Locator("app-filter-bar").GetByLabel("Category")).ToHaveValueAsync("Streets");
        // The card shows Streets citywide; the slower panel can only rank neighborhoods within it.
        await Expect(page.Locator(".hover-card .scope")).ToHaveTextAsync("Streets · Whole city · last 30 days");
        await Expect(page.Locator(".hover-card .headline dd")).ToHaveTextAsync("2.5 days");
        await Expect(page.Locator("app-slower-panel .sentence")).ToContainTextAsync("Within Streets.");
        await Expect(page.Locator("app-slower-panel").GetByRole(AriaRole.Button, new() { Name = "Categories" })).ToBeDisabledAsync();
    }

    [Fact]
    public async Task A_shared_link_opens_the_drawer_and_the_daily_chart()
    {
        var page = await host.NewPageAsync();

        var backlog = page.WaitForResponseAsync(r => r.Url.Contains("/api/backlog", StringComparison.Ordinal) && r.Url.Contains("grain=day", StringComparison.Ordinal));
        await page.GotoAsync(new Uri(host.BaseUrl, "?window=30&neighborhood=downtown&range=90d").ToString());

        Assert.Equal(200, (await backlog).Status);
        await Expect(page.GetByRole(AriaRole.Dialog).Locator("h2")).ToHaveTextAsync("Downtown");
        await Expect(page.Locator("app-filter-bar").GetByLabel("Neighborhood")).ToHaveValueAsync("downtown");
        await Expect(page.Locator("app-filter-bar").GetByLabel("Backlog chart")).ToHaveValueAsync("90d");
        await Expect(page.Locator("app-backlog-chart .chart")).ToHaveAttributeAsync("aria-label", new Regex("^Backlog chart. Daily from "));
    }
}
