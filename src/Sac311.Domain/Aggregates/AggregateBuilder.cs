using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Aggregates;

/// <summary>
/// Computes everything the API serves, in one pass over the requests (<see cref="Add"/>) and then <see cref="Build"/>.
/// Non-service requests (<see cref="AggregateRequest.IsService"/>) are counted in <see cref="AggregateSet.RequestCount"/>
/// only; usp_build_exclusions reports them separately. The rest give:
/// <list type="bullet">
/// <item>per window (<see cref="Windows"/> days) and period (current, prior), per cell of neighborhood × district ×
/// category (each possibly "all", so 8 grouping sets): opened, closed, excluded and bulk-closed counts, and the median and p90 days
/// to close over the metric-eligible requests closed in the period;</item>
/// <item>per cell: the requests open at the as-of time and the median of their ages;</item>
/// <item>per day from <see cref="BacklogStart"/>, per district × category: opened, closed and open at day's end.</item>
/// </list>
/// Percentiles follow <c>PERCENTILE_CONT</c> exactly (<see cref="Percentile.Cont"/>). They are computed here rather
/// than in SQL because the window sorts take about a minute on SQL Server Express (see docs/tradeoffs.md).
/// </summary>
public sealed class AggregateBuilder
{
    public static readonly IReadOnlyList<int> Windows = [30, 90, 365];

    /// <summary>The source keeps requests updated since 2024-01-01 only, so the backlog series starts there.</summary>
    public static readonly DateOnly BacklogStart = new(2024, 1, 1);

    // Internal ids: -1 is "no value" in a request and "all" in a cell; district 0 likewise.
    private const int None = -1;
    private const byte NoDistrict = 0;

    private static readonly Dims[] AllSets = [.. Enumerable.Range(0, 8).Select(i => (Dims)i)];
    private static readonly Dims[] BacklogSets = [Dims.None, Dims.District, Dims.Category, Dims.District | Dims.Category];

    private readonly DateTime _asOfUtc;
    private readonly int _asOfDay;
    private readonly int _backlogStartDay;
    private readonly int _backlogDays;
    private readonly int _horizonDay;

    private readonly Dictionary<string, int> _slugIds = new(StringComparer.Ordinal);
    private readonly List<string> _slugs = [];
    private readonly Dictionary<string, int> _groupIds = new(StringComparer.Ordinal);
    private readonly List<string> _groups = [];

    private readonly List<Valued> _closed = [];
    private readonly List<Valued> _open = [];
    private readonly Dictionary<PeriodCell, Counts> _counts = [];
    private readonly Dictionary<Cell, BacklogSeries> _backlog = [];
    private int _requests;

    public AggregateBuilder(DateTime asOfUtc)
    {
        _asOfUtc = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc);
        AsOfDate = Pacific.ToLocalDate(_asOfUtc);
        _asOfDay = AsOfDate.DayNumber;
        _backlogStartDay = BacklogStart.DayNumber;
        _backlogDays = Math.Max(0, _asOfDay - _backlogStartDay + 1);
        _horizonDay = _asOfDay - (2 * Windows.Max());
    }

    /// <summary>The Sacramento date of the as-of time: the last day of every current period.</summary>
    public DateOnly AsOfDate { get; }

    public void Add(in AggregateRequest request)
    {
        _requests++;
        if (!request.IsService)
        {
            return;
        }

        var cell = new Cell(Id(request.NeighborhoodSlug, _slugIds, _slugs), request.DistrictNumber ?? NoDistrict, Id(request.CategoryGroup, _groupIds, _groups));
        var created = request.CreatedLocal?.DayNumber;
        var leftBacklog = request.BacklogCloseLocal?.DayNumber;
        var counted = request.IsMetricEligible && request.DaysToClose is not null && request.ClosedLocal is not null;

        for (var w = 0; w < Windows.Count; w++)
        {
            if (PeriodOf(created, Windows[w]) is { } openedIn)
            {
                Count(new PeriodCell(w, openedIn, cell)).Opened++;
            }

            if (!counted && request.Status == StatusGroup.Closed && PeriodOf(leftBacklog, Windows[w]) is { } excludedIn)
            {
                Count(new PeriodCell(w, excludedIn, cell)).Excluded++;
            }

            if (counted && request.IsBulkClosure && PeriodOf(request.ClosedLocal!.Value.DayNumber, Windows[w]) is { } bulkIn)
            {
                Count(new PeriodCell(w, bulkIn, cell)).BulkClosed++;
            }
        }

        if (counted && request.ClosedLocal!.Value.DayNumber is var closed && closed > _horizonDay && closed <= _asOfDay)
        {
            _closed.Add(new Valued(cell, closed, Hundredths(request.DaysToClose!.Value)));
        }

        if (created is not { } c || c > _asOfDay)
        {
            return;
        }

        var stillOpen = leftBacklog is not { } l || l > _asOfDay;
        if (stillOpen && request.CreatedUtc is { } createdUtc)
        {
            _open.Add(new Valued(cell, c, Hundredths(Math.Max(0m, (decimal)(_asOfUtc - createdUtc).TotalDays))));
        }

        if (_backlogDays > 0)
        {
            var key = new Cell(None, cell.District, cell.Group);
            if (!_backlog.TryGetValue(key, out var series))
            {
                series = new BacklogSeries(_backlogDays);
                _backlog.Add(key, series);
            }

            series.Add(c - _backlogStartDay, stillOpen ? null : leftBacklog!.Value - _backlogStartDay);
        }
    }

    public AggregateSet Build() => new(_asOfUtc, AsOfDate, _requests, BuildWindows(), BuildOpen(), BuildBacklog());

    private List<WindowStats> BuildWindows()
    {
        // Counts are kept per finest cell; roll them up to every grouping set.
        var counts = new Dictionary<PeriodCell, Counts>();
        foreach (var (key, c) in _counts)
        {
            foreach (var dims in AllSets)
            {
                if (Project(key.Cell, dims) is { } cell)
                {
                    var total = counts.TryGetValue(key with { Cell = cell }, out var t) ? t : new Counts();
                    total.Opened += c.Opened;
                    total.Excluded += c.Excluded;
                    total.BulkClosed += c.BulkClosed;
                    counts[key with { Cell = cell }] = total;
                }
            }
        }

        // Medians: values sorted once, then each (window, grouping set) pass appends to already-sorted lists.
        _closed.Sort(static (a, b) => a.Hundredths.CompareTo(b.Hundredths));
        var percentiles = new Dictionary<PeriodCell, (int Count, decimal Median, decimal P90)>();
        for (var w = 0; w < Windows.Count; w++)
        {
            foreach (var dims in AllSets)
            {
                var lists = new Dictionary<PeriodCell, List<int>>();
                foreach (var row in _closed)
                {
                    if (PeriodOf(row.Day, Windows[w]) is { } period && Project(row.Cell, dims) is { } cell)
                    {
                        var key = new PeriodCell(w, period, cell);
                        if (!lists.TryGetValue(key, out var list))
                        {
                            list = [];
                            lists.Add(key, list);
                        }

                        list.Add(row.Hundredths);
                    }
                }

                foreach (var (key, list) in lists)
                {
                    percentiles[key] = (list.Count, Percentile.Cont(list, 0.5m), Percentile.Cont(list, 0.9m));
                }
            }
        }

        var result = new List<WindowStats>(counts.Count);
        foreach (var key in counts.Keys.Union(percentiles.Keys))
        {
            var c = counts.TryGetValue(key, out var x) ? x : new Counts();
            var hasValues = percentiles.TryGetValue(key, out var p);
            result.Add(new WindowStats(
                Windows[key.Window], key.Current ? AggregatePeriod.Current : AggregatePeriod.Prior, ToCell(key.Cell),
                c.Opened, hasValues ? p.Count : 0, c.Excluded, c.BulkClosed, hasValues ? p.Median : null, hasValues ? p.P90 : null));
        }

        return result;
    }

    private List<OpenStats> BuildOpen()
    {
        _open.Sort(static (a, b) => a.Hundredths.CompareTo(b.Hundredths));
        var result = new List<OpenStats>();
        foreach (var dims in AllSets)
        {
            var lists = new Dictionary<Cell, List<int>>();
            foreach (var row in _open)
            {
                if (Project(row.Cell, dims) is { } cell)
                {
                    if (!lists.TryGetValue(cell, out var list))
                    {
                        list = [];
                        lists.Add(cell, list);
                    }

                    list.Add(row.Hundredths);
                }
            }

            result.AddRange(lists.Select(kv => new OpenStats(ToCell(kv.Key), kv.Value.Count, Percentile.Cont(kv.Value, 0.5m))));
        }

        return result;
    }

    private List<BacklogDay> BuildBacklog()
    {
        var rolled = new Dictionary<Cell, BacklogSeries>();
        foreach (var (key, series) in _backlog)
        {
            foreach (var dims in BacklogSets)
            {
                if (Project(key, dims) is { } cell)
                {
                    if (!rolled.TryGetValue(cell, out var total))
                    {
                        total = new BacklogSeries(_backlogDays);
                        rolled.Add(cell, total);
                    }

                    total.AddFrom(series);
                }
            }
        }

        var result = new List<BacklogDay>(rolled.Count * _backlogDays);
        foreach (var (key, series) in rolled)
        {
            var cell = ToCell(key);
            var open = series.OpenBefore;
            for (var d = 0; d < _backlogDays; d++)
            {
                open += series.Opened[d] - series.Closed[d];
                result.Add(new BacklogDay(BacklogStart.AddDays(d), cell.DistrictNumber, cell.CategoryGroup, series.Opened[d], series.Closed[d], open));
            }
        }

        return result;
    }

    /// <summary>Whether a local day falls in the current (true) or prior (false) period of a window, or neither (null).</summary>
    private bool? PeriodOf(int? day, int windowDays) => day switch
    {
        null => null,
        var d when d > _asOfDay => null,
        var d when d > _asOfDay - windowDays => true,
        var d when d > _asOfDay - (2 * windowDays) => false,
        _ => null,
    };

    /// <summary>A finest cell as seen by a grouping set; null when the request lacks a value the set groups by.</summary>
    private static Cell? Project(Cell cell, Dims dims)
    {
        var byNeighborhood = dims.HasFlag(Dims.Neighborhood);
        var byDistrict = dims.HasFlag(Dims.District);
        if ((byNeighborhood && cell.Slug == None) || (byDistrict && cell.District == NoDistrict))
        {
            return null;
        }

        return new Cell(byNeighborhood ? cell.Slug : None, byDistrict ? cell.District : NoDistrict, dims.HasFlag(Dims.Category) ? cell.Group : None);
    }

    private AggregateCell ToCell(Cell cell) => new(
        cell.Slug == None ? AggregateCell.All : _slugs[cell.Slug],
        cell.District,
        cell.Group == None ? AggregateCell.All : _groups[cell.Group]);

    private Counts Count(PeriodCell key)
    {
        if (!_counts.TryGetValue(key, out var c))
        {
            c = new Counts();
            _counts.Add(key, c);
        }

        return c;
    }

    private static int Id(string? value, Dictionary<string, int> ids, List<string> values)
    {
        if (value is null)
        {
            return None;
        }

        if (!ids.TryGetValue(value, out var id))
        {
            id = values.Count;
            ids.Add(value, id);
            values.Add(value);
        }

        return id;
    }

    private static int Hundredths(decimal days) => (int)Math.Round(days * 100m, MidpointRounding.AwayFromZero);

    [Flags]
    private enum Dims
    {
        None = 0,
        Neighborhood = 1,
        District = 2,
        Category = 4,
    }

    private readonly record struct Cell(int Slug, byte District, int Group);

    private readonly record struct PeriodCell(int Window, bool Current, Cell Cell);

    private readonly record struct Valued(Cell Cell, int Day, int Hundredths);

    private sealed class Counts
    {
        public int Opened { get; set; }

        public int Excluded { get; set; }

        public int BulkClosed { get; set; }
    }

    /// <summary>Per-day opened and closed counts from <see cref="BacklogStart"/>, plus how many were open before it.</summary>
    private sealed class BacklogSeries(int days)
    {
        public int[] Opened { get; } = new int[days];

        public int[] Closed { get; } = new int[days];

        public int OpenBefore { get; private set; }

        /// <param name="created">Created day, relative to the start (negative: before it).</param>
        /// <param name="closed">Day it left the backlog, relative to the start; null if still open at the as-of date.</param>
        public void Add(int created, int? closed)
        {
            if (created < 0)
            {
                OpenBefore++;
            }
            else
            {
                Opened[created]++;
            }

            if (closed is { } x)
            {
                if (x < 0)
                {
                    OpenBefore--;
                }
                else
                {
                    Closed[x]++;
                }
            }
        }

        public void AddFrom(BacklogSeries other)
        {
            OpenBefore += other.OpenBefore;
            for (var i = 0; i < Opened.Length; i++)
            {
                Opened[i] += other.Opened[i];
                Closed[i] += other.Closed[i];
            }
        }
    }
}
