<#
.SYNOPSIS
    Profiles the live City of Sacramento 311 ArcGIS feed and writes docs/source-profile.md.

.DESCRIPTION
    Read-only. Measures the facts the pipeline design depends on (row count, key uniqueness,
    paging cost, date ranges, sentinel and inconsistent dates, blank strings, category and
    channel values, geometry bounds), downloads the neighborhood boundaries to
    data/geo/sacramento-neighborhoods.geojson and measures how well the 311 neighborhood
    names match them. Anything between the "manual" markers in the existing profile
    (license notes, decisions) is kept across reruns.

    Works on Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
    ./scripts/verify-source.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceUrl = 'https://services5.arcgis.com/54falWtcpty3V47Z/arcgis/rest/services/SalesForce311_View/FeatureServer/0',
    [string]$ItemId = '5b9a9448663f41b1898643b6d91201c4',
    [string]$NeighborhoodsUrl = 'https://services5.arcgis.com/54falWtcpty3V47Z/arcgis/rest/services/Neighborhoods/FeatureServer/0',
    [string]$NeighborhoodsItemId = '49f20f1612ae4f0a9292eb65f8bd4013',
    [string]$OutFile,
    [string]$GeoJsonOut,
    # Share of non-null neighborhood rows that must match a polygon to use the neighborhood map.
    [double]$MatchThreshold = 0.95
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
# $PSScriptRoot is empty inside param defaults on Windows PowerShell 5.1.
if (-not $OutFile) { $OutFile = Join-Path $PSScriptRoot '..\docs\source-profile.md' }
if (-not $GeoJsonOut) { $GeoJsonOut = Join-Path $PSScriptRoot '..\data\geo\sacramento-neighborhoods.geojson' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

# Same bbox as Geo.Validate in Sac311.Domain (lon/lat, WGS84).
$Bbox = @{ XMin = -122.0; YMin = 38.2; XMax = -120.9; YMax = 39.0 }
$Inv = [Globalization.CultureInfo]::InvariantCulture

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(120)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('rivercity-pulse-verify/1.0')

# ---------------------------------------------------------------- helpers

function Invoke-ArcGis {
    <# POSTs form-encoded params (long GET URLs return IIS 404) and returns parsed JSON plus timing. #>
    param([string]$Url, [hashtable]$Params = @{})
    $form = @{ f = 'json' }
    foreach ($k in $Params.Keys) { $form[$k] = [string]$Params[$k] }
    $pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
    foreach ($k in $form.Keys) { $pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' $k, $form[$k])) }
    $content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $resp = $http.PostAsync($Url, $content).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $sw.Stop()
    if (-not $resp.IsSuccessStatusCode) { throw "HTTP $([int]$resp.StatusCode) from $Url" }

    $json = $body | ConvertFrom-Json
    # ArcGIS reports many failures as HTTP 200 with an error body.
    if ($json.PSObject.Properties.Name -contains 'error') {
        throw "ArcGIS error from ${Url}: $($json.error.code) $($json.error.message) where=$($form['where'])"
    }
    [pscustomobject]@{ Json = $json; Ms = $sw.ElapsedMilliseconds; Bytes = [Text.Encoding]::UTF8.GetByteCount($body) }
}

function Get-Count([string]$Where) {
    (Invoke-ArcGis "$ServiceUrl/query" @{ where = $Where; returnCountOnly = 'true' }).Json.count
}

function Get-Stats([string]$Where, [object[]]$Stats) {
    $p = @{ where = $Where; outStatistics = (ConvertTo-Json -Compress -InputObject $Stats) }
    (Invoke-ArcGis "$ServiceUrl/query" $p).Json.features[0].attributes
}

function Get-GroupCounts([string]$Field, [string]$Where = '1=1') {
    $p = @{
        where                      = $Where
        groupByFieldsForStatistics = $Field
        outStatistics              = '[{"statisticType":"count","onStatisticField":"OBJECTID","outStatisticFieldName":"n"}]'
        orderByFields              = 'n DESC'
    }
    foreach ($f in (Invoke-ArcGis "$ServiceUrl/query" $p).Json.features) {
        [pscustomobject]@{ Value = $f.attributes.$Field; Count = [int64]$f.attributes.n }
    }
}

function ConvertFrom-EsriDate($ms) {
    if ($null -eq $ms) { return $null }
    [DateTimeOffset]::FromUnixTimeMilliseconds([int64]$ms).UtcDateTime
}

function Format-N($n) { ([int64]$n).ToString('N0', $Inv) }
function Format-Pct([double]$part, [double]$whole) {
    if ($whole -eq 0) { return 'n/a' }
    ($part / $whole).ToString('P1', $Inv)
}
function Format-Date($d) { if ($null -eq $d) { 'null' } else { $d.ToString('yyyy-MM-dd HH:mm', $Inv) + ' UTC' } }
function Format-Value($v) {
    if ($null -eq $v) { return '*(null)*' }
    if ($v -eq '') { return "*(empty)*" }
    '`' + ($v -replace '\|', '\|' -replace '`', "'") + '`'
}
# Same idea as MapKey.For in Sac311.Domain: lowercase, alphanumerics only.
function Get-MapKey([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    ($s.ToLowerInvariant() -replace '[^a-z0-9]', '')
}
function Get-SqlUtc([datetime]$d) { "TIMESTAMP '" + $d.ToString('yyyy-MM-dd HH:mm:ss', $Inv) + "'" }

$md = New-Object System.Text.StringBuilder
function Add-Line([string]$s = '') { [void]$md.AppendLine($s) }
function Add-Table([string[]]$Headers, [object[]]$Rows) {
    Add-Line ('| ' + ($Headers -join ' | ') + ' |')
    Add-Line ('|' + (($Headers | ForEach-Object { '---' }) -join '|') + '|')
    foreach ($r in $Rows) { Add-Line ('| ' + ($r -join ' | ') + ' |') }
    Add-Line
}

$started = Get-Date
Write-Host "Profiling $ServiceUrl"

# ---------------------------------------------------------------- service + layer metadata

$layer = (Invoke-ArcGis $ServiceUrl).Json
$item = (Invoke-ArcGis "https://www.arcgis.com/sharing/rest/content/items/$ItemId").Json
$nbItem = (Invoke-ArcGis "https://www.arcgis.com/sharing/rest/content/items/$NeighborhoodsItemId").Json
$lastEdit = if ($layer.editingInfo -and $layer.editingInfo.lastEditDate) { ConvertFrom-EsriDate $layer.editingInfo.lastEditDate } else { $null }

# ---------------------------------------------------------------- counts and keys

Write-Host '  counts and keys'
$total = Get-Count '1=1'
$refBlank = Get-Count "ReferenceNumber IS NULL OR ReferenceNumber = ''"
$distinctRef = $null
try {
    $distinctRef = (Invoke-ArcGis "$ServiceUrl/query" @{
            where = '1=1'; outFields = 'ReferenceNumber'; returnDistinctValues = 'true'; returnCountOnly = 'true'
        }).Json.count
} catch { Write-Warning "Distinct ReferenceNumber count not supported: $_" }
$oid = Get-Stats '1=1' @(
    @{ statisticType = 'min'; onStatisticField = 'OBJECTID'; outStatisticFieldName = 'lo' },
    @{ statisticType = 'max'; onStatisticField = 'OBJECTID'; outStatisticFieldName = 'hi' })
$sample = (Invoke-ArcGis "$ServiceUrl/query" @{ where = '1=1'; outFields = 'ReferenceNumber'; resultRecordCount = 1; returnGeometry = 'false' }).Json.features[0].attributes.ReferenceNumber

# ---------------------------------------------------------------- paging cost

Write-Host '  paging cost'
$pageSize = [int]$layer.maxRecordCount
$keyset = Invoke-ArcGis "$ServiceUrl/query" @{
    where = "OBJECTID > $([int64]$oid.lo - 1)"; outFields = '*'; orderByFields = 'OBJECTID'
    resultRecordCount = $pageSize; outSR = 4326
}
$keysetRows = @($keyset.Json.features).Count
$deepOffset = [int64]([math]::Floor($total * 0.95))
$offset = Invoke-ArcGis "$ServiceUrl/query" @{
    where = '1=1'; outFields = '*'; orderByFields = 'OBJECTID'
    resultOffset = $deepOffset; resultRecordCount = $pageSize; outSR = 4326
}
$pages = [math]::Ceiling($total / $pageSize)

# ---------------------------------------------------------------- dates

Write-Host '  dates'
$dateFields = 'DateCreated', 'DateUpdated', 'DateClosed'
$nowUtc = [DateTime]::UtcNow
$dateRows = foreach ($f in $dateFields) {
    $s = Get-Stats '1=1' @(
        @{ statisticType = 'min'; onStatisticField = $f; outStatisticFieldName = 'lo' },
        @{ statisticType = 'max'; onStatisticField = $f; outStatisticFieldName = 'hi' })
    [pscustomobject]@{
        Field  = $f
        Min    = ConvertFrom-EsriDate $s.lo
        Max    = ConvertFrom-EsriDate $s.hi
        Nulls  = Get-Count "$f IS NULL"
        Pre2000 = Get-Count "$f < TIMESTAMP '2000-01-01 00:00:00'"
        Future = Get-Count "$f > $(Get-SqlUtc $nowUtc.AddDays(1))"
    }
}
$updated24h = Get-Count "DateUpdated > $(Get-SqlUtc $nowUtc.AddDays(-1))"
$closedBeforeCreated = Get-Count 'DateClosed < DateCreated'
$closedNoDate = Get-Count "PublicStatus = 'CLOSED' AND DateClosed IS NULL"
$openWithDate = Get-Count "PublicStatus IN ('NEW','IN PROGRESS') AND DateClosed IS NOT NULL"

# ---------------------------------------------------------------- strings

Write-Host '  strings and categories'
$textFields = 'Address', 'CrossStreet', 'CategoryLevel1', 'CategoryLevel2', 'CategoryName', 'SourceLevel1', 'Neighborhood', 'ZIP', 'CouncilDistrictNumber', 'SFTicketID'
$blankRows = foreach ($f in $textFields) {
    $n = Get-Count "$f IS NULL"
    $e = Get-Count "$f = ''"
    [pscustomobject]@{ Field = $f; Nulls = $n; Empty = $e }
}
$junkAddress = Get-Count "UPPER(Address) IN ('N/A','NA','TBD','OK','ZOOM','NONE','UNKNOWN')"
$zipBad = $null
try { $zipBad = Get-Count "ZIP IS NOT NULL AND ZIP <> '' AND CHAR_LENGTH(ZIP) <> 5" } catch { Write-Warning "ZIP length check not supported: $_" }

$status = @(Get-GroupCounts 'PublicStatus')
$district = @(Get-GroupCounts 'CouncilDistrictNumber')
$cat1 = @(Get-GroupCounts 'CategoryLevel1')
$cat2 = @(Get-GroupCounts 'CategoryLevel2')
$source = @(Get-GroupCounts 'SourceLevel1')
$dataSource = @(Get-GroupCounts 'Data_Source')
$hoods = @(Get-GroupCounts 'Neighborhood')

# ---------------------------------------------------------------- geometry

Write-Host '  geometry'
$envelope = @{ xmin = $Bbox.XMin; ymin = $Bbox.YMin; xmax = $Bbox.XMax; ymax = $Bbox.YMax; spatialReference = @{ wkid = 4326 } }
$inBbox = (Invoke-ArcGis "$ServiceUrl/query" @{
        where = '1=1'; geometry = (ConvertTo-Json -Compress $envelope); geometryType = 'esriGeometryEnvelope'
        inSR = 4326; spatialRel = 'esriSpatialRelIntersects'; returnCountOnly = 'true'
    }).Json.count

# ---------------------------------------------------------------- neighborhood boundaries

Write-Host '  neighborhood boundaries'
$geoDir = Split-Path -Parent $GeoJsonOut
if (-not (Test-Path $geoDir)) { New-Item -ItemType Directory -Force $geoDir | Out-Null }
$geoResp = Invoke-ArcGis "$NeighborhoodsUrl/query" @{
    where = '1=1'; outFields = 'NAME'; outSR = 4326; geometryPrecision = 6; f = 'geojson'
}
$polygons = @($geoResp.Json.features)
$geoText = ConvertTo-Json -Depth 32 -Compress -InputObject $geoResp.Json
[IO.File]::WriteAllText((Resolve-Path $geoDir).Path + '\' + (Split-Path -Leaf $GeoJsonOut), $geoText, (New-Object Text.UTF8Encoding($false)))

$polyNames = @($polygons | ForEach-Object { $_.properties.NAME })
$polyKeys = @{}
foreach ($n in $polyNames) { $polyKeys[(Get-MapKey $n)] = $n }
$polyExact = @{}
foreach ($n in $polyNames) { $polyExact[$n] = $true }

$named = @($hoods | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Value) })
$namedRows = ($named | Measure-Object Count -Sum).Sum
$hoodMatch = foreach ($h in $named) {
    $exact = $polyExact.ContainsKey($h.Value)
    $key = Get-MapKey $h.Value
    [pscustomobject]@{
        Name = $h.Value; Count = $h.Count; Exact = $exact
        Normalized = $exact -or $polyKeys.ContainsKey($key)
        Polygon = if ($polyKeys.ContainsKey($key)) { $polyKeys[$key] } else { $null }
    }
}
$exactNames = @($hoodMatch | Where-Object Exact).Count
$normNames = @($hoodMatch | Where-Object Normalized).Count
$normRows = (@($hoodMatch | Where-Object Normalized) | Measure-Object Count -Sum).Sum
if ($null -eq $normRows) { $normRows = 0 }
$matchRate = if ($namedRows) { $normRows / $namedRows } else { 0 }
$usedKeys = @{}
foreach ($m in $hoodMatch) { if ($m.Polygon) { $usedKeys[$m.Polygon] = $true } }
$orphanPolygons = @($polyNames | Where-Object { -not $usedKeys.ContainsKey($_) } | Sort-Object)
$mapDecision = if ($matchRate -ge $MatchThreshold) {
    "**Use the neighborhood choropleth.** $(Format-Pct $normRows $namedRows) of rows with a neighborhood match a polygon after normalization (threshold $(Format-Pct $MatchThreshold 1)). Unmatched names go in ``ref.neighborhood_alias``."
} else {
    "**Fall back to council districts.** Only $(Format-Pct $normRows $namedRows) of rows with a neighborhood match a polygon after normalization (threshold $(Format-Pct $MatchThreshold 1))."
}

# ---------------------------------------------------------------- write markdown

Write-Host '  writing report'
$manualStart = '<!-- manual:start -->'
$manualEnd = '<!-- manual:end -->'
$manual = $null
if (Test-Path $OutFile) {
    $existing = [IO.File]::ReadAllText((Resolve-Path $OutFile).Path)
    $i = $existing.IndexOf($manualStart); $j = $existing.IndexOf($manualEnd)
    if ($i -ge 0 -and $j -gt $i) { $manual = $existing.Substring($i, $j + $manualEnd.Length - $i) }
}
if (-not $manual) {
    $manual = "$manualStart`n## License and terms (manual)`n`n_Not reviewed yet._`n$manualEnd"
}

Add-Line '# Source profile: Sacramento 311'
Add-Line
Add-Line "Generated by ``scripts/verify-source.ps1`` on $($started.ToUniversalTime().ToString('yyyy-MM-dd HH:mm', $Inv)) UTC. Rerun the script to refresh; only the section between the ``manual`` markers is hand-written and is kept across runs."
Add-Line

Add-Line '## Service'
Add-Line
Add-Table @('Property', 'Value') @(
    , @('Item', "[$($item.title)](https://www.arcgis.com/home/item.html?id=$ItemId)")
    , @('Layer URL', "``$ServiceUrl``")
    , @('Layer name', $layer.name)
    , @('Owner', $item.owner)
    , @('Item description', ($item.snippet -replace '\|', '/'))
    , @('Max records per page', $pageSize)
    , @('Supports pagination', $layer.advancedQueryCapabilities.supportsPagination)
    , @('Supports statistics', $layer.advancedQueryCapabilities.supportsStatistics)
    , @('Native spatial reference', "$($layer.extent.spatialReference.latestWkid)")
    , @('Last edit', (Format-Date $lastEdit))
)

Add-Line '### Fields (schema contract)'
Add-Line
Add-Table @('Field', 'Type', 'Length') @($layer.fields | ForEach-Object {
        , @("``$($_.name)``", ($_.type -replace '^esriFieldType', ''), $(if ($_.PSObject.Properties.Name -contains 'length') { $_.length } else { '' }))
    })

Add-Line '## Volume and keys'
Add-Line
Add-Table @('Check', 'Result') @(
    , @('Row count', (Format-N $total))
    , @('ReferenceNumber null or blank', (Format-N $refBlank))
    , @('Distinct ReferenceNumber', $(if ($null -ne $distinctRef) { "$(Format-N $distinctRef) ($(Format-Pct $distinctRef $total) of rows)" } else { 'not supported' }))
    , @('Sample ReferenceNumber', "``$sample``")
    , @('OBJECTID range', "$(Format-N $oid.lo) to $(Format-N $oid.hi) (span $(Format-N ($oid.hi - $oid.lo + 1)), density $(Format-Pct $total ($oid.hi - $oid.lo + 1)))")
    , @('Updated in the last 24 h', (Format-N $updated24h))
)

Add-Line '## Paging cost'
Add-Line
Add-Table @('Query', 'Rows', 'Time', 'Payload (decompressed)') @(
    , @("Keyset ``OBJECTID > min-1``", $keysetRows, "$($keyset.Ms) ms", "$([math]::Round($keyset.Bytes / 1MB, 2)) MB")
    , @("Offset $(Format-N $deepOffset)", @($offset.Json.features).Count, "$($offset.Ms) ms", "$([math]::Round($offset.Bytes / 1MB, 2)) MB")
)
Add-Line "A full keyset backfill is about **$(Format-N $pages) pages** of $pageSize rows, roughly $([math]::Round($pages * $keyset.Ms / 60000, 1)) minutes of fetch time at the measured keyset speed."
Add-Line

Add-Line '## Dates'
Add-Line
Add-Table @('Field', 'Min', 'Max', 'NULL', 'Before 2000', 'Future (> now + 1 day)') @($dateRows | ForEach-Object {
        , @($_.Field, (Format-Date $_.Min), (Format-Date $_.Max), (Format-N $_.Nulls), (Format-N $_.Pre2000), (Format-N $_.Future))
    })
Add-Table @('Consistency check', 'Rows') @(
    , @('DateClosed < DateCreated', (Format-N $closedBeforeCreated))
    , @('CLOSED with no DateClosed', (Format-N $closedNoDate))
    , @('NEW / IN PROGRESS with a DateClosed', (Format-N $openWithDate))
)

Add-Line '## Status'
Add-Line
Add-Table @('PublicStatus', 'Rows', 'Share') @($status | ForEach-Object { , @((Format-Value $_.Value), (Format-N $_.Count), (Format-Pct $_.Count $total)) })

Add-Line '## Blank and junk strings'
Add-Line
Add-Line "ArcGIS counts '' as non-null, so both are listed."
Add-Line
Add-Table @('Field', 'NULL', "Empty ('')", 'Blank share') @($blankRows | ForEach-Object {
        , @("``$($_.Field)``", (Format-N $_.Nulls), (Format-N $_.Empty), (Format-Pct ($_.Nulls + $_.Empty) $total))
    })
Add-Line "Address junk tokens (N/A, NA, TBD, OK, ZOOM, NONE, UNKNOWN): **$(Format-N $junkAddress)** rows."
Add-Line
Add-Line "ZIP values that are not 5 characters: **$(if ($null -ne $zipBad) { Format-N $zipBad } else { 'check not supported' })**."
Add-Line

Add-Line '## Council district'
Add-Line
Add-Table @('CouncilDistrictNumber', 'Rows') @($district | ForEach-Object { , @((Format-Value $_.Value), (Format-N $_.Count)) })

Add-Line "## Categories ($($cat1.Count) level-1 values, $($cat2.Count) level-2 values)"
Add-Line
Add-Table @('CategoryLevel1', 'Rows') @($cat1 | ForEach-Object { , @((Format-Value $_.Value), (Format-N $_.Count)) })

Add-Line "## Source channel ($($source.Count) values)"
Add-Line
Add-Table @('SourceLevel1', 'Rows') @($source | ForEach-Object { , @((Format-Value $_.Value), (Format-N $_.Count)) })
Add-Table @('Data_Source', 'Rows') @($dataSource | ForEach-Object { , @((Format-Value $_.Value), (Format-N $_.Count)) })

Add-Line '## Geometry'
Add-Line
Add-Line "Rows inside the bbox ($($Bbox.XMin), $($Bbox.YMin), $($Bbox.XMax), $($Bbox.YMax)): **$(Format-N $inBbox)** of $(Format-N $total) ($(Format-Pct $inBbox $total)). The rest have no point or a point outside it."
Add-Line

Add-Line '## Neighborhoods vs. boundary file'
Add-Line
Add-Line "Boundaries: [$($nbItem.title)](https://www.arcgis.com/home/item.html?id=$NeighborhoodsItemId) ($($polygons.Count) polygons, saved to ``data/geo/$(Split-Path -Leaf $GeoJsonOut)``, WGS84, 6-decimal precision)."
Add-Line
Add-Table @('Check', 'Result') @(
    , @('Distinct 311 neighborhood names (non-blank)', $named.Count)
    , @('Rows with a neighborhood', (Format-N $namedRows))
    , @('Rows with NULL or blank neighborhood', (Format-N ($total - $namedRows)))
    , @('Names matching a polygon exactly', "$exactNames of $($named.Count)")
    , @('Names matching after normalization (lowercase, alphanumerics only)', "$normNames of $($named.Count)")
    , @('Row-weighted match rate (normalized)', "**$(Format-Pct $normRows $namedRows)**")
    , @('Polygons with no 311 name', $orphanPolygons.Count)
)
Add-Line "Decision: $mapDecision"
Add-Line
$unmatched = @($hoodMatch | Where-Object { -not $_.Normalized } | Sort-Object Count -Descending)
if ($unmatched.Count) {
    Add-Line '### 311 names with no polygon'
    Add-Line
    Add-Table @('Neighborhood', 'Rows') @($unmatched | ForEach-Object { , @((Format-Value $_.Name), (Format-N $_.Count)) })
}
$normOnly = @($hoodMatch | Where-Object { $_.Normalized -and -not $_.Exact } | Sort-Object Name)
if ($normOnly.Count) {
    Add-Line '### Names that match only after normalization'
    Add-Line
    Add-Table @('311 name', 'Polygon NAME', 'Rows') @($normOnly | ForEach-Object { , @((Format-Value $_.Name), (Format-Value $_.Polygon), (Format-N $_.Count)) })
}
if ($orphanPolygons.Count) {
    Add-Line '### Polygons with no 311 name'
    Add-Line
    Add-Line (($orphanPolygons | ForEach-Object { Format-Value $_ }) -join ', ')
    Add-Line
}

Add-Line '## Item metadata (license fields)'
Add-Line
Add-Table @('Item', 'licenseInfo', 'accessInformation', 'Tags') @(
    , @('311 calls', $(if ($item.licenseInfo) { 'set (see item page)' } else { '*(blank)*' }), $(if ($item.accessInformation) { $item.accessInformation } else { '*(blank)*' }), (($item.tags | Select-Object -First 12) -join ', '))
    , @('Neighborhoods', $(if ($nbItem.licenseInfo) { 'set (see item page)' } else { '*(blank)*' }), $(if ($nbItem.accessInformation) { $nbItem.accessInformation } else { '*(blank)*' }), (($nbItem.tags | Select-Object -First 12) -join ', '))
)

Add-Line $manual

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
[IO.File]::WriteAllText((Join-Path (Resolve-Path $outDir).Path (Split-Path -Leaf $OutFile)), $md.ToString().Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
$http.Dispose()

Write-Host ("Done in {0:N0} s. Rows {1}, neighborhood match {2}. Wrote {3}" -f ((Get-Date) - $started).TotalSeconds, (Format-N $total), (Format-Pct $normRows $namedRows), $OutFile)
