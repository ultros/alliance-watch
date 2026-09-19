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

Use **RUN ACTIVE SCAN** to run a collection cycle (per-feed due times and backoff still
apply), **ASSESSMENT CONSOLE** or click the gauge for the assessment views, and
**OPEN CONFIGURATION** to edit `config.json`. Restart after configuration changes.
Double-click a record for its evidence panel and source link. The alert center shows
deduplicated assessment rules; historical article scores remain available as legacy data.

Every newly discovered article is also archived inside SQLite. The
`article_archives` table stores losslessly gzip-compressed full response HTML and
extracted article text. `article_images` stores linked gzip-compressed image blobs
with their source URL, MIME type, byte sizes, order, and alt text. Existing article
rows are backfilled in a bounded background worker, while failed downloads are recorded
and retried on later collection cycles. Set `archive_enabled` to `false` to disable
new full-page/image downloads. Exports always omit full article text and images.

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
