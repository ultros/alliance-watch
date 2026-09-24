# AllianceWatch Strategic Assessment Console

Alliance Watch monitors official defense, security, cyber, and global news sources for alliance shifts, force-posture changes, joint exercises, basing access, command integration, logistics, and other geopolitical escalation indicators.

AllianceWatch is now a native C# Windows desktop application with a borderless,
full-screen strategic monitoring interface. Version 3 adds a deterministic escalation
assessment engine with separate risk, confidence and momentum, 30 configurable protocols,
source-family deduplication, historical replay and append-only assessments. The existing
SQLite articles, matches and archives remain intact. The global index is **not a probability
of war or a prediction of its timing**.

See [IMPLEMENTATION.md](IMPLEMENTATION.md) for scoring rules, configuration, verification,
capacity limits and analytical limitations.

The repository ships without a populated database. On first launch, AllianceWatch
creates an empty `alliance_watch.db` and applies its schema migrations. Databases,
archives, logs, credentials and generated test artifacts are excluded from Git.

## Run the C# application

Requirements: Windows 10/11 and the .NET 8 SDK or Desktop Runtime.

```powershell
dotnet restore
dotnet run --project AllianceWatch.csproj
```

The interface starts in borderless full-screen mode. Use the header controls to move
to the next monitor, minimize, restore to a resizable window, or close. Drag the
`ALLIANCEWATCH` title area to move a restored window between screens. `F11` toggles
full-screen, `Ctrl+Shift+Left/Right` moves between monitors, and `Esc` exits.
At shorter window heights, scroll the right rail to reach every operator action;
filter bars in the secondary windows wrap to keep their controls accessible.

Use **RUN ACTIVE SCAN** to run a collection cycle (per-feed due times and backoff still
apply), **ASSESSMENT CONSOLE** or click the gauge for the assessment views, and
**OPEN CONFIGURATION** to edit `config.json`. Restart after configuration changes.
Double-click a record for its evidence panel and source link. The alert center shows
new actionable protocol signals alongside deduplicated assessment rules. Dismissing a
signal acknowledges that alert without changing its historical record.

**INDEX CONTEXT** replaces the recent-article sparkline with 6-hour, 24-hour and
7-day index changes and the two largest current evidence contributors. Each signal
shows its share of raw evidence weight, confidence and reporting origins; click it
to inspect the supporting records. Percentages describe evidence weight before
convergence and final scaling. A dash means the historical baseline is unavailable.
The left rail scrolls on smaller screens.

The search box above the main news table (or `Ctrl+F`) opens **ALL-ARTICLE ARCHIVE
SEARCH**. It searches the complete local history, not just recent dashboard rows,
including ignored articles, headline/source/date fields, linked signals and evidence,
image metadata, and saved compressed article text. Enter runs a literal search;
leave the box empty to page through all articles. Searches are cancellable and show
progress. Select a result to preview its locally archived text or open its full
database record.

Right-click a news row in the main table or **EVIDENCE SEARCH** to **Ignore this
article for score**. The action prompts for an optional reason and immediately
recalculates the current index. It hides that article from the main news list but
does not delete its original article, normalized evidence, or past assessments.
Use **IGNORED NEWS / RESTORE** in Operator Control to search and restore excluded
articles. The database browser also exposes active ignored news and an append-only
ignore/restore log. Because reports are grouped into event families, excluding one
article may leave a score contribution from other independent articles.

Every newly discovered article is also archived inside SQLite. The
`article_archives` table stores losslessly gzip-compressed full response HTML and
extracted article text. `image_blobs` stores each distinct compressed image once by
SHA-256; `article_images` keeps the links to every article, plus source URL, MIME type,
byte sizes, order, and alt text. Existing article
rows are backfilled in a bounded background worker, while failed downloads are recorded
and retried with backoff so an unavailable site cannot stall the queue. The worker
keeps processing between feed scans, downloading up to six articles concurrently.
Set `archive_concurrency` to a value from 1 to 12 to adjust this limit, or
`archive_enabled` to `false` to disable new full-page/image downloads. Restart after
changing these settings. Slow or failed images are skipped while retaining the
downloaded page and successful images. The pending count refreshes between scans;
it includes failed pages waiting for a scheduled retry. Exports always omit full
article text and images.

Open **DATABASE BROWSER** to inspect retained records and archived images. Its search
checks the full selected dataset, and table controls show the total row count and let
you jump directly to a page. In **ARCHIVED IMAGES**, switch to **GALLERY VIEW** to
scroll every image matching the current date, search, and article scope. The gallery
starts with **UNIQUE IMAGES**; switch to **ARTICLE LINKS** to see every occurrence. Thumbnails
load near the visible area and remain in a bounded memory cache. Single-click an
image for its preview; double-click it or press Enter to see all linked articles in
the table (or the exact occurrence in **ARTICLE LINKS** mode). The article dropdown lists recent entries; the adjacent field accepts an
image ID or full article hash anywhere in the archive. `Ctrl+F` focuses search,
`F5` refreshes, and Escape clears search.

For older archives that still contain inline image payloads, close the app and run
`dotnet AllianceWatch.dll --dedupe-images` from the deployed app folder. This makes a
verified timestamped backup, moves duplicate payloads to the shared store in resumable
batches, checks that every image link resolves, and compacts the live SQLite file.
Keep the backup until you have reviewed the migrated gallery; the backup itself still
occupies disk space.

Open **OPERATIONS WORKSPACE** for eight follow-on workflows: theatre coverage and
feed freshness; the change since the prior completed scan; side-by-side claim-family
reports; a personal theatre/actor/protocol watchlist; append-only analyst reviews;
data-quality warnings; a local what-if lab; and verified backup/restore-copy checks.
The small footer badge opens coverage directly. `F5` reloads the workspace.
Claim comparison searches the entire recent in-memory result and displays 250 families
per page. Linked corrections appear with the original family when the parent record
is within the loaded window. The workspace refuses a silent partial result if its
100,000-recent-record capacity is exceeded.

Watchlist alerts require a newly scored or materially strengthened event with raw
score at least 1 and evidence confidence at or above the chosen threshold. They do
not back-alert on existing records when a watch is added. Analyst review flags are
separate from, and never rewrite, collected evidence or live assessments. What-if
replay adjusts only selected settings and stored source-quality inputs in memory;
it does not forecast events or save a new live score.

**CREATE VERIFIED BACKUP** uses SQLite's consistent backup mechanism, runs an
integrity/schema check and never overwrites an existing file. **TEST RESTORE TO
COPY** creates another verified database at a new path, leaving the live database
untouched. Use **INSPECT VERIFIED BACKUP** to browse it read-only. This is not an
in-place live restore; keep the backup in a separate safe location.

`config.json` contains regional feeds for Asia, Europe, the Middle East, Africa,
North America, Central America, and South America. The first three are the main
display focus. Central America covers Belize, Guatemala, Honduras, El Salvador,
Nicaragua, Costa Rica, and Panama. Its regional scenario requires two named
regional actors; an isolated country mention does not activate it. The new
theatre has local journalism, an official Panama Canal feed, and an English
discovery feed. Spanish-language feeds are collected, but the assessment rules
are primarily English-language, so those articles may require manual review.
Feed health is visible in the app; a missing or delayed feed can affect coverage.

To build a release executable:

```powershell
dotnet build AllianceWatch.csproj -c Release
```

The resulting application is in `bin\Release\net8.0-windows\`. The C# app reads
the same `config.json` and schema-compatible `alliance_watch.db` used by the legacy
Python monitor, so existing history remains visible.

## Verification

```powershell
dotnet build AllianceWatch.csproj -c Release
dotnet bin/Release/net8.0-windows/AllianceWatch.dll --self-test
dotnet bin/Release/net8.0-windows/AllianceWatch.dll --benchmark
dotnet bin/Release/net8.0-windows/AllianceWatch.dll --ui-smoke
```

The smoke mode uses `assessment-ui-smoke.db`, clearly marked synthetic data, and no
network collection. Tests and benchmarks use temporary databases and simulated HTTP.
Reports are written to `assessment-test-results.txt` and `assessment-benchmark.txt`.

## Legacy Python monitor

AllianceWatch is a lightweight Windows RSS monitor for language suggesting a shift from ordinary geopolitical cooperation toward formal defense obligations, integrated command, wartime logistics, or coordinated military planning. It stores articles and matches locally in SQLite and does not open or execute downloaded content.

## Requirements and install

- Windows 10 or 11
- Python 3.12 or newer

Open PowerShell in this folder and install the three dependencies:

```powershell
py -m pip install -r requirements.txt
```

## Test the Windows popup

```powershell
python alliance_watch.py --test-alert
```

The message should say `AllianceWatch test successful.`

Alert text is selectable. Drag across any portion and press `Ctrl+C`, use
`Ctrl+A` followed by `Ctrl+C`, or click **Copy All** to copy the full alert.

## Run once

```powershell
python alliance_watch.py --once
```

## Run continuously

```powershell
python alliance_watch.py
```

Press `Ctrl+C` to stop cleanly. Add `--debug` to either run command to also print detailed errors to the console.

## Configure feeds

Edit `config.json` in a text editor. Each feed needs a display name, an HTTP/HTTPS RSS URL, and a weight from 0 to 10:

```json
{
  "name": "Example official source",
  "url": "https://example.gov/news/rss.xml",
  "weight": 10
}
```

Use a weight near 10 for official government or defense sources, 8 for major wire services, 6 for established international outlets, and 3 for aggregators. `poll_minutes` controls the interval. `max_popups_per_cycle` limits sequential popups without preventing database or logfile records.

The included Google News feeds are discovery sources, not endorsements of the underlying publishers. Review and replace the starter list as desired. RSS availability and publisher terms can change.

## Scoring and tuning

The base score is the strongest phrase category found: RED 10, ORANGE 7, YELLOW 4, or baseline GREEN 1. The program then adds the strongest configured actor combination, 0–3 points derived from source weight, and bonuses for high-value combinations such as mutual defense plus integrated command. Historical, opinion, and hypothetical wording subtracts 3. Source weight or actor names without a matched indicator always score zero.

Edit these clearly labeled sections near the top of `alliance_watch.py`:

- `PHRASE_GROUPS` for indicator wording
- `ACTOR_PATTERNS` for country/organization aliases
- `SUPPRESSION_TERMS` for contextual downgrades
- `YELLOW_THRESHOLD`, `ORANGE_THRESHOLD`, and `RED_THRESHOLD` for alert tiers
- `actor_combination_score()` and `calculate_score()` for scoring rules

SQLite data is created automatically in `alliance_watch.db`. The normal text log is `alliance_watch.log`. JSON arrays in the `matches.matched_phrases` and `matched_actors` columns preserve Unicode and make later review straightforward.

## Optional: start at Windows login

1. Open **Task Scheduler** and choose **Create Basic Task**.
2. Name it `AllianceWatch` and select **When I log on**.
3. Choose **Start a program**.
4. For **Program/script**, enter the full path to `pythonw.exe` (often found beside `python.exe`). Use `python.exe` instead if you want a console window.
5. For **Add arguments**, enter the full quoted path to `alliance_watch.py`.
6. For **Start in**, enter this project folder without quotes.
7. Finish, then use **Run** once to verify it starts.

The logged-on user must have permission to write in the project folder. Task Scheduler can be used to stop or disable the task later.

## Notes

- Each RSS GUID (or a SHA-256 fallback) becomes a stable local identifier.
- An article is inserted only once, so it cannot generate a second alert after restart.
- Failed feeds and malformed individual entries are logged while the remaining feeds continue.
- Alerts are sorted RED, ORANGE, then YELLOW and by score within each tier.
