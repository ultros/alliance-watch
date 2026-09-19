# AllianceWatch expansion

The active implementation is the C# Windows desktop application. The Python monitor
is retained as a legacy program and does not run the new assessment engine.

## Operator workflow

Click the global gauge or **ASSESSMENT CONSOLE**. The console includes escalation
vector, why-score-moved, evidence search, scenarios, timeline, narrative intensity,
actor graph, regional hotspots, feed health, backtest, assessment log, rule alerts,
protocols, exports, historical baselines, derivatives, and metadata import/settings.

Double-click evidence, a ledger event, timeline row/point, or actor relationship to
inspect its underlying record. The record panel includes original timestamps, URL,
publisher/origin, source class, canonical hashes, parser version, actors, scenario
membership, protocol IDs, source family, correction candidates and score components.

Risk display bands are LOW 0–19, GUARDED 20–39, ELEVATED 40–59, HIGH 60–79 and
EXTREME 80–100. Risk and confidence are independent channels. Missing history is
reported explicitly; zero confidence does not establish safety.

## Deterministic rules

For each event family:

```
raw = severity × corroboration × novelty × relevance × recency
      × (1 − contradiction) × (1 − missing-companion moderation)
recency = 0.5 ^ (age_hours / protocol_half_life_hours)
confidence = 100 × source_quality × corroboration
             × (1 − contradiction) × (1 − missing-companion moderation)
index = 100 × (1 − exp(−raw_total / normalization_scale))
```

The default normalization scale is 60. Source quality affects confidence rather
than multiplying risk. A single originating report has a corroboration multiplier
of 0.5; two verified independent origins yield 0.75 and three yield 1.0. Merely
publishing a copy on another website does not increase this count. Identical content,
canonical URLs, normalized titles, bounded title/body similarity and wire attribution
are used conservatively. This is lexical deduplication, not a validated semantic model.

Repeated actor/protocol activity in the preceding 30 days reduces novelty to 0.7.
Unattributed actors reduce relevance to 0.5. Missing logistics/draw-down companions
within 72 hours moderates selected mobilization/dispersal/ultimatum signals by 15%.
This absence is a coverage-sensitive moderation, never proof of safety.

Retractions contribute zero. Disputed wording, candidate correction links and
conflicting reported troop/casualty/equipment figures reduce support. Linked
correction-only records cannot add risk. Feed changes under the same GUID create
new article versions instead of overwriting the original record.

Convergence requires protocol diversity, independently corroborated event families,
a common scenario, common actors/geography and a 72-hour window. Each additional
protocol beyond two adds the configured bonus (default 2 raw points). Contributions
shared by multiple scenarios are apportioned so they are not counted repeatedly in
the global index. Global risk derives from scenario raw totals. Broad actor mentions,
article volume and narrative terms do not add automatic extra bonuses.

Every assessment includes raw and normalized scores, prior-change components,
confidence, scenarios, protocol IDs, source record IDs, rule version and a hash of
the exact settings. The normalization-adjustment row ensures the ledger sums to the
displayed index change. Evidence, assessments and saved rules are append-only through
SQLite triggers. Compact derived history rows keep timeline reads small.

## Configuration

Existing `config.json` remains valid. Optional top-level fields:

```json
{
  "archive_enabled": false,
  "assessment": {
    "Version": "AW-1.0",
    "NormalizationScale": 60,
    "ConvergenceBonus": 2,
    "AlertThreshold": 60,
    "ConfidenceAlertThreshold": 70
  }
}
```

Merge these fields into the existing object; retain its `feeds` and polling options.
The `assessment` object also accepts `Protocols`, `Scenarios`, `ActorAliases` and
`NarrativeTerms`. The complete default definitions are visible in IMPORT / SETTINGS
and live in `ProtocolCatalog.cs` / `AssessmentEngine.cs`. Replacing `Protocols`
requires all 30 stable IDs. Protocol patterns and exclusions are bounded regular
expressions with timeouts. Each protocol carries severity, half-life, corroboration
requirements, source requirements, actor/geography constraints and rule version.
Changing detection patterns applies to subsequently normalized records; immutable
older extraction results retain their original classifications. Weight and half-life
changes apply when making a new assessment, leaving previous assessments intact.

Each feed accepts:

```json
{
  "name": "Example source",
  "url": "https://example.org/feed",
  "weight": 5,
  "enabled": true,
  "interval_minutes": 10,
  "group": "Official notices",
  "source_class": "A",
  "source_quality": 0.8,
  "origin": "Example issuing organization",
  "original_reporting": false,
  "language": "en",
  "adapter": "rss"
}
```

Classes are A official, B structured data, C journalism, D user-defined. Set
`original_reporting` only after establishing the origin; it is not an ideological
trust label. Defaults are class C, quality 0.5, unknown language and unverified
independence. Official releases establish what an organization stated, not the
truth of every underlying claim. Existing `weight` is retained for legacy records.

Adapters support RSS, Atom, JSON, CSV and manual watch URLs. JSON supports an
`items_path` (dot-separated) and `field_map` mapping `title`, `summary`, `url`, `id`,
`published` to provider fields. Optional adapter labels `gdelt`, `acled`, `ucdp`
select conventional JSON envelopes; endpoint URLs, field maps, access rights and
credentials must be supplied for the actual provider response. These are configurable
JSON adapters, not end-to-end validated integrations with those live services.
`bearer_token_env` reads a bearer token from an environment variable. No credentials
are embedded in source code. RSS is decoded safely; XML DTDs are prohibited. The
malformed-feed recovery is intentionally limited to bare ampersands.

CSV/JSON metadata imports are available in the console. They require title, optional
summary, URL, ID and publication time. Imports receive the current ingestion time.
They cannot retroactively become available in an earlier historical replay.

## Replay and calibration

Backtest uses both publication and first-seen cutoffs. Later denials and corrections
are excluded until actually available. Playback speed, scrubbing, protocol activations
and full score components are visible. Evaluation notes can record false positives,
missed indicators and delayed detections. Replay applies the configured scoring rules
to stored extraction results and never rewrites live assessments.

The ladder is a conservative descriptive rule classification. Keyword evidence cannot
establish regional/systemic war: states 7–10 are not automatically asserted. Actor graph
edges are explicitly labeled co-mentions with detected relationship classes, not
verified directional relationships. Hotspot coordinates are scenario-region centers,
not extracted event coordinates. Unknown source country/language and actor roles are
not invented. Narrative baselines use observed days and unique event-family shares;
fewer than seven observed days or zero variance produces unavailable statistics.

## Reliability, storage and performance

Migrations add versioned tables alongside the original schema; no database reset is
required. WAL, busy timeouts, timestamp/hash/cluster indexes, batch normalization
transactions and serialized assessment writes preserve consistency. Collection uses
four workers, shared HTTP connections, 25-second timeouts, conditional requests,
rate-limit handling and exponential backoff. HTTP response size is capped at 8 MiB;
parsers accept up to 5,000 items per response. Archival work runs separately with one
article worker, bounded images and a per-article time budget. Diagnostic logs rotate
at 10 MiB; audit rows are not rotated away.

Evidence search displays 250 rows at a time, with debounced text filtering. The console
loads at most 100,000 normalized records and 60,000 compact assessment summaries.
The scoring path refuses to publish a partial assessment if the 100,000-record working
capacity is exceeded; it reports an error and preserves the archive. Historical rows
beyond the console window remain stored. Very large archives need a further streaming
assessment/query implementation. These capacity bounds are not claims of indefinite
unlimited operation.

The benchmark exercises extraction/scoring, 10,000 hash lookups, 100 simulated feed
requests with 1,000 new articles, normalization/deduplication, a 100,000-article SQLite
archive, text search, dashboard queries/layout and repeated conditional polling.
See `assessment-benchmark.txt` for measured results. Live-provider latency and multi-day
memory behavior have not been validated. Source-specific authentication, multilingual
semantic extraction, independently verified actor-role relationships, exact event
geocoding and exhaustive contradiction/date reasoning remain outside this rule-based
implementation; this build should not be represented as a validated warning model.

## Verification

Run the commands in README.md. The suite covers all 30 positive protocol fixtures,
exclusions, aliases, scenarios, RSS/Atom/malformed XML, JSON/CSV parsing, URL normalization,
syndication, provenance independence, convergence, negative evidence, confidence,
decay, corrections/retractions, no future-data leakage, ledger reconciliation, exports,
idempotent migrations, immutable assessments, conditional HTTP, worker bounds, feed
failure isolation and alert deduplication. Synthetic UI mode leaves the production
database untouched. The benchmark uses temporary files and simulated HTTP only.
