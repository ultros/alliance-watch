using System.Runtime.CompilerServices;

namespace AllianceWatch;

/// <summary>
/// Central, opt-in-on-demand help for the desktop client. Holding Ctrl keeps dense
/// operational screens uncluttered while making every control explainable.
/// </summary>
internal static class UiToolTips
{
    private static readonly ConditionalWeakTable<Form, TooltipSession> Sessions = new();
    private static readonly (string Term, string Definition)[] TooltipTerms =
    [
        ("archive search", "Archive search: a read-only, literal search through every stored article, including older and ignored items, linked signal/evidence fields, image metadata, and compressed saved article text. It does not change the score."),
        ("archived text", "Archived text: extracted article content retained locally in compressed form. It can be searched and previewed even when the original web page is no longer available."),
        ("coverage", "Coverage: the currency and diversity of collected reporting in a theatre. Missing coverage is an information gap, not evidence of safety."),
        ("blind spot", "Blind spot: an area where sources are absent, stale, or insufficiently independent for a strong conclusion."),
        ("fresh feed", "Fresh feed: a configured source with a successful fetch within the greater of one hour or twice its polling interval."),
        ("verified origin", "Verified origin: a distinct original-reporting organization in configured source metadata; copied reports are not independent origins."),
        ("last scan", "Last scan: the previous completed collection cycle. Startup and manual-import assessments are not silently treated as scans."),
        ("claim comparison", "Claim comparison: records grouped around one reported event, including linked corrections, disputes, and retractions. Grouping is not verification."),
        ("personal watchlist", "Personal watchlist: operator-selected theatres, actors, or protocols that generate local alerts only on qualifying new or strengthened evidence."),
        ("analyst review", "Analyst review: an append-only human judgment attached to an event family; it does not rewrite the original evidence or live score."),
        ("ignored for score", "Ignored for score: an operator excluded one article from future assessments. The original article and evidence remain archived, and other reports in its event family may still contribute."),
        ("score exclusion", "Score exclusion: a reversible article-level choice to omit a report from new score calculations and the main news list. Each ignore or restore action is logged."),
        ("false positive", "False positive: a report or detected match an analyst judges not to represent the intended indicator. The review remains separate from collected data."),
        ("data quality", "Data quality: diagnostic checks for stale sources, suspect timestamps, duplication, concentration, and abrupt score changes."),
        ("what-if", "What-if: a local same-time sensitivity test using simulated rules or source quality. It does not save a live assessment or predict events."),
        ("verified backup", "Verified backup: a new self-contained SQLite copy that passed integrity and required-schema checks without replacing the live database."),
        ("restore copy", "Restore copy: a second database created from a verified backup and checked independently; the running database is not overwritten."),
        ("quick check", "SQLite quick check: a database structural-integrity check. It does not validate the truth or completeness of source reports."),
        ("qualified evidence", "Qualified evidence: material kept by the app after it passes its configured text and source checks."),
        ("retained evidence", "Retained evidence: material stored in the app’s local database for later review."),
        ("independent sources", "Independent sources: different organizations that gathered or first reported information, rather than copies of one report."),
        ("independent source", "Independent source: an organization separate from the one that first reported a copied item."),
        ("source origin", "Source origin: the organization that first gathered or published a report."),
        ("source quality", "Source quality: a saved setting that influences how a source stream is handled; it does not guarantee that any claim is true."),
        ("source handling", "Source handling: the app’s saved treatment of a source for tracing, duplicate detection, and scoring."),
        ("source stream", "Source stream: one configured flow of items from a publisher, feed, or import."),
        ("event families", "Event families: groups of records about the same reported event, used so repeated coverage is not counted repeatedly."),
        ("event family", "Event family: one group of records about the same reported event."),
        ("risk index", "Risk index: the app’s 0–100 descriptive priority score; it is not a probability or forecast."),
        ("raw score", "Raw score: the calculation total before the app applies final scaling or normalization."),
        ("theater anchor", "Theater anchor: a broad regional marker for organizing reports; it is not an incident or unit location."),
        ("protocol families", "Protocol families: broad groups of related matching rules, such as diplomatic or force/logistics."),
        ("protocol family", "Protocol family: a broad group of related matching rules."),
        ("protocol tag", "Protocol tag: the numbered rule label attached when an item matches configured wording and source conditions."),
        ("protocols", "Protocols: numbered, versioned rules that flag configured wording for user review."),
        ("protocol", "Protocol: a numbered, versioned rule that flags configured wording for user review."),
        ("corroboration", "Corroboration: separate organizations reporting support for the same event; it improves support but does not prove a claim."),
        ("provenance", "Provenance: the saved source, URL, dates, and processing trail showing where an item came from."),
        ("first-seen", "First-seen time: when this app first stored an item; it can be later than the source publication time."),
        ("publication time", "Publication time: the date and time supplied by the source for its item."),
        ("deduplication", "Deduplication: grouping copies and near-identical reports so one story does not increase counts more than once."),
        ("normalization", "Normalization: putting source material into one consistent local format for comparison."),
        ("recency", "Recency: how recently an item was published or stored; older items contribute less as they age."),
        ("half-life", "Half-life: the configured time over which an item’s calculation contribution falls by half."),
        ("assessment", "Assessment: a timestamped calculation from the records available to the app at that time."),
        ("scenario", "Scenario: a saved monitoring lens that groups actors and places; it is not a claim that the scenario is occurring."),
        ("theater", "Theater: a broad geographic monitoring area, not a confirmed area of operations."),
        ("categories", "Category: a broad analytic group used to organize matching rules."),
        ("category", "Category: a broad analytic group used to organize matching rules."),
        ("alert state", "Alert state: whether a local review prompt is unread, acknowledged, muted, or snoozed."),
        ("alert", "Alert: a local prompt that an item or rule condition merits review; it is not a verified warning of a real-world event."),
        ("UTC", "UTC: the zero-offset global time standard used here for cross-region comparison; GMT and Zulu are treated as UTC in this app."),
        ("weight", "Weight: a saved calculation value that controls how strongly a matching item affects an internal score."),
        ("contribution", "Contribution: the amount an individual item adds to a calculation under the saved rules."),
        ("convergence", "Convergence: the degree to which several distinct kinds of matched evidence appear together in the current calculation."),
        ("momentum", "Momentum: the recent direction of the app’s internal index when enough past assessments exist."),
        ("configuration", "Configuration: the local saved settings that control feeds, matching rules, scenarios, and display behavior."),
        ("configured", "Configured: set in the app’s saved local settings."),
        ("feed", "Feed: a configured source that supplies new items to the app."),
        ("collection", "Collection: retrieving items from configured sources into the local app."),
        ("filter", "Filter: a display rule that limits what is shown without deleting stored information."),
        ("record", "Record: one stored local item, such as an article, evidence event, alert, or assessment row."),
        ("source", "Source: the publisher, feed, or import from which an item entered the app."),
        ("index", "Index: an internal calculated indicator used to organize review priority; it is not a probability."),
        ("confidence", "Confidence: the app’s measure of support in the material it collected; it does not measure complete real-world coverage."),
        ("severity", "Severity: the saved display priority associated with a matching rule or alert; it is not a verified threat level."),
        ("signal", "Signal: an item that matched a configured rule and merits review; it is not a factual finding by itself."),
        ("rule", "Rule: a saved condition the app uses to match text, classify records, or create an alert."),
        ("replay", "Replay: recalculating an assessment at an earlier UTC time using only records available by then."),
        ("baseline", "Baseline: a past comparison period used to put a current measurement in context."),
        ("archive", "Archive: locally stored historical material, such as a fetched article copy or image."),
        ("local database", "Local database: the app’s on-device storage for collected records, assessments, and archives."),
        ("metadata", "Metadata: descriptive fields about a stored item, such as its ID, title, date, MIME type, size, and source link; it is separate from the image or article payload itself."),
        ("virtual gallery", "Virtual gallery: a scrolling display that represents the complete matching result set without creating a separate on-screen control for every item."),
        ("thumbnail", "Thumbnail: a small local preview generated from an archived image so the browser can show many images efficiently."),
        ("thumbnail cache", "Thumbnail cache: a bounded in-memory set of recently visible image previews. Older previews are released automatically and can be regenerated from the local archive when needed."),
        ("cache", "Cache: temporary in-memory data kept to make a repeated display operation faster; it is not a new permanent record."),
        ("scope", "Scope: the currently selected subset of local records, such as one article, a date range, or a search result."),
        ("parameterized", "Parameterized query: a database query that keeps entered search text separate from query instructions, preventing the text from changing the query structure."),
        ("page", "Page: one bounded portion of a larger table result. The status line reports the full matching count and the current page position."),
        ("MIME type", "MIME type: a standardized label for a file format, such as image/jpeg or image/png."),
        ("article hash", "Article hash: the app’s stable local identifier for an article version, used to connect its record, archive, and images.")
    ];

    public static void Enable(Form form)
    {
        if (Sessions.TryGetValue(form, out _)) return;
        var session = new TooltipSession(form);
        Sessions.Add(form, session);
        session.Attach(form);
        form.FormClosed += (_, _) =>
        {
            session.Dispose();
            Sessions.Remove(form);
        };
    }

    private sealed class TooltipSession : IDisposable
    {
        private readonly ToolTip _toolTip = new()
        {
            InitialDelay = 0,
            ReshowDelay = 0,
            AutoPopDelay = 18000,
            ShowAlways = true
        };
        private readonly HashSet<Control> _attached = [];
        private string? _shownText;
        private Control? _shownOn;

        public TooltipSession(Form _) { }

        public void Attach(Control control)
        {
            if (!_attached.Add(control)) return;
            control.ControlAdded += (_, eventArgs) =>
            {
                if (eventArgs.Control is { } added) Attach(added);
            };
            control.MouseMove += (_, eventArgs) => ShowControlHelp(control, eventArgs.Location);
            control.MouseLeave += (_, _) => Hide();
            foreach (Control child in control.Controls) Attach(child);

            if (control is DataGridView grid) AttachGrid(grid);
            if (control is TabControl tabs) AttachTabs(tabs);
        }

        private void AttachGrid(DataGridView grid)
        {
            grid.MouseMove += (_, eventArgs) =>
            {
                if (!CtrlHeld()) { Hide(); return; }
                var hit = grid.HitTest(eventArgs.X, eventArgs.Y);
                if (hit.ColumnIndex < 0) { ShowControlHelp(grid, eventArgs.Location); return; }
                var column = grid.Columns[hit.ColumnIndex];
                var header = string.IsNullOrWhiteSpace(column.HeaderText) ? column.Name : column.HeaderText;
                var text = hit.Type == DataGridViewHitTestType.ColumnHeader
                    ? ColumnHelp(header)
                    : hit.RowIndex >= 0
                        ? $"{header}\n\nThis field belongs to the selected operational record. Values are retained as collected or calculated in this application; use the selected-record view and source link to inspect provenance and context."
                        : ColumnHelp(header);
                Show(grid, text, eventArgs.Location);
            };
        }

        private void AttachTabs(TabControl tabs)
        {
            tabs.MouseMove += (_, eventArgs) =>
            {
                if (!CtrlHeld()) { Hide(); return; }
                for (var index = 0; index < tabs.TabCount; index++)
                {
                    if (!tabs.GetTabRect(index).Contains(eventArgs.Location)) continue;
                    var title = tabs.TabPages[index].Text;
                    Show(tabs, $"{title}\n\nThis tab is a focused local view. Explicit Save, Add, scan, import, backup, or alert actions may write separate records or files; simply changing tabs does not alter evidence or scores.", eventArgs.Location);
                    return;
                }
                Hide();
            };
        }

        private void ShowControlHelp(Control control, Point location)
        {
            if (!CtrlHeld()) { Hide(); return; }
            Show(control, Describe(control), location);
        }

        private void Show(Control control, string text, Point location)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = ExpandDefinitions(text);
            if (ReferenceEquals(_shownOn, control) && _shownText == text) return;
            _shownOn = control;
            _shownText = text;
            _toolTip.Show(text, control, new Point(Math.Min(location.X + 16, Math.Max(4, control.Width - 28)), Math.Min(location.Y + 18, Math.Max(4, control.Height - 24))), 18000);
        }

        private void Hide()
        {
            if (_shownOn is not null) _toolTip.Hide(_shownOn);
            _shownOn = null;
            _shownText = null;
        }

        public void Dispose()
        {
            _toolTip.Dispose();
            _attached.Clear();
        }
    }

    private static bool CtrlHeld() => (Control.ModifierKeys & Keys.Control) == Keys.Control;

    public static string ExpandDefinitions(string text)
    {
        var definitions = new List<string>();
        foreach (var (term, definition) in TooltipTerms.OrderByDescending(item => item.Term.Length))
        {
            if (!text.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (definitions.Contains(definition, StringComparer.Ordinal)) continue;
            definitions.Add(definition);
        }

        return definitions.Count == 0
            ? text
            : text + "\n\nTERMS USED HERE\n" + string.Join("\n", definitions.Select(definition => "• " + definition));
    }

    private static string Describe(Control control)
    {
        if (control.Tag as string == "metric-detail") return string.Empty; // MapForm supplies row-specific definitions.
        if (control is TelemetryPanel panel) return PanelHelp(panel.Caption);
        if (control is ThreatMeter) return "GLOBAL INDICATOR LEVEL\n\nThe 0–100 readout is a descriptive internal index derived from configured, qualified public-report evidence. It is not a probability, prediction, intelligence estimate, or declaration that conflict will occur. Click it to open the full Assessment Console.";
        if (control is IndexContextPanel) return "INDEX CONTEXT\n\nHistorical changes in index points and the two largest current evidence contributions. Evidence-weight percentages compare event families before convergence and final scaling. Click a leading signal to inspect its supporting records. A dash means the historical comparison is unavailable.";
        if (control is VirtualImageGallery) return "VIRTUAL IMAGE GALLERY\n\nDisplays every image matching the current local scope in one continuously scrollable gallery. The browser retains lightweight image metadata for the complete result set while decoding only nearby thumbnails into a bounded cache. Single-click a card to inspect and preview it; double-click it, or press Enter, to open that image's article scope in the table. Arrow keys, Page Up/Down, Home, and End navigate cards.";
        if (control is TheaterMapControl) return "GLOBAL THEATER MAP\n\nNatural Earth coastlines provide geographic context. Theater markers are broad analytic anchors, not event coordinates, unit locations, or force tracking. Click a marker for supporting records; category rings show qualified protocol families.";
        if (control is AssessmentPlot) return "ASSESSMENT HISTORY PLOT\n\nOrange traces the descriptive risk index and cyan traces evidence confidence. Click a point to inspect that timestamped assessment. Neither line predicts future events.";
        if (control is ActorGraphControl) return "ACTOR GRAPH\n\nLines connect actors co-mentioned in retained evidence with a detected protocol. They do not establish command, intent, coordination, direction, or a verified relationship.";
        if (control is RegionalHotspotControl) return "REGIONAL HOTSPOTS\n\nThis small schematic aggregates recent event families by configured geography label. The dots are regional centers, not incident coordinates or force positions.";
        if (control is WindowIconButton icon) return icon.Icon switch
        {
            WindowIcon.Close => "CLOSE WINDOW\n\nCloses this application window. Collection state and retained records remain in the local database.",
            WindowIcon.Minimize => "MINIMIZE\n\nSends the application window to the taskbar without stopping the configured collection timer.",
            WindowIcon.Maximize => "MAXIMIZE\n\nExpands the window to use the available display area.",
            WindowIcon.Restore => "RESTORE WINDOW\n\nReturns a maximized window to its previous size.",
            _ => "MOVE TO NEXT DISPLAY\n\nMoves the console to the next available monitor."
        };
        if (control is DataGridView) return "DATA TABLE\n\nHold Ctrl over a column header for a field-specific definition. Click a header to sort when sorting is enabled; select a row to inspect its full retained fields and provenance.";
        if (control is TextBox { PlaceholderText: "Search entire selected dataset…" })
            return "DATASET SEARCH\n\nSearches the full selected local database view, not just the currently displayed page. The query is parameterized and read-only. Results retain the normal date and image-scope filters; Ctrl+F focuses this field and Escape clears it.";
        if (control is TextBox { PlaceholderText: "Image ID or article hash" })
            return "IMAGE ID OR ARTICLE HASH\n\nEnter an archived image ID to focus its parent article, or enter a full article hash to scope the image view. This lookup works across the complete local archive, including records outside the recent-article dropdown. Press Enter to apply it.";
        if (control.Tag as string == "review-notes")
            return "ANALYST REVIEW NOTES\n\nExplain why you chose the review outcome and cite source links if useful. The Save Review action appends this judgment to a separate audit log; it does not edit the article, evidence event, or score.";
        if (control is TextBox textBox)
            return string.IsNullOrWhiteSpace(textBox.PlaceholderText)
                ? "TEXT FIELD\n\nEditable text input. Changes only affect the current form or filter unless an explicit Save, Import, or configuration action is used."
                : $"{textBox.PlaceholderText}\n\nEnter text to narrow or identify records in the current view. This is a local display filter or lookup; it does not change the underlying database.";
        if (control.Tag as string == "image-grouping")
            return "IMAGE GROUPING\n\nUnique Images shows one card per distinct stored image payload, even if that image appeared in many articles. Article Links shows each article-image relationship separately. Double-click a unique card to inspect all of its linked articles in the paged table. This only changes the display; article evidence and provenance remain intact.";
        if (control is ComboBox comboBox)
            return $"SELECTION: {comboBox.Text}\n\nChoose a scope, sort order, scenario, or other display option. Selection changes affect the current view and do not modify stored evidence.";
        if (control is DateTimePicker)
            return "DATE / TIME FILTER\n\nConstrains the current view to records at or within the selected UTC time boundary. It is a display filter and does not remove retained records.";
        if (control is NumericUpDown)
            return "NUMERIC THRESHOLD\n\nAdjusts the current query, replay, or display parameter. It does not alter the assessment rules unless you explicitly save a configuration change.";
        if (control is CheckBox checkBox)
            return $"{checkBox.Text}\n\nToggles this filter or option for the current view. It does not change the underlying record classification.";
        if (control is Button button) return ButtonHelp(button.Text);
        if (control is PictureBox) return "IMAGE PREVIEW\n\nShows an archived image selected from the read-only database browser. Use the accompanying record details to inspect MIME type, source URL, size, and associated article.";
        if (control is Label label && !string.IsNullOrWhiteSpace(label.Text)) return LabelHelp(label.Text);
        if (control is TabPage page) return $"{page.Text}\n\nThis page presents one focused view within the current console. Hold Ctrl over table headers and controls for field-level help.";
        return "CONSOLE AREA\n\nHold Ctrl while hovering an item for contextual help. Read-only displays show retained evidence or derived views; actions are labeled explicitly.";
    }

    private static string PanelHelp(string caption)
    {
        var key = caption.Trim().ToUpperInvariant();
        return key switch
        {
            "GLOBAL INDICATOR LEVEL" => "GLOBAL INDICATOR LEVEL\n\nA descriptive 0–100 composite of configured, qualified evidence. It organizes monitoring priority; it is not a probability, forecast, or declaration of war.",
            "ESCALATION LADDER // DESCRIPTIVE" => "ESCALATION LADDER\n\nA rule-based descriptive classification of the current evidence picture. Higher labels require more stringent verification. This panel never treats a headline alone as proof of escalation.",
            "ACTOR SIGNAL LINKS // LAST 72H" => "ACTOR SIGNAL LINKS\n\nRecent co-mentions of actors in detected evidence. A link does not by itself show a treaty, command relationship, coordination, intent, or responsibility.",
            "INDEX CONTEXT // LEADING SIGNALS" => "INDEX CONTEXT\n\nShows historical index changes and the two strongest current evidence contributors. Shares use raw evidence weight before convergence and scaling. Click a signal for its records; a dash means there is no saved baseline for that interval.",
            "DETECTED ALIGNMENT SIGNALS" => "DETECTED ALIGNMENT SIGNALS\n\nRetained source records that matched configured protocols. Double-click a row for evidence; Ctrl-hover the column headers for definitions. A match is a review cue, not a factual finding.",
            "WHAT CHANGED // EVIDENCE BRIEFING" => "EVIDENCE BRIEFING\n\nA short explanation of material changes in the current assessment. It summarizes retained evidence and should be checked against source records for context.",
            "GLOBAL FLASHPOINT MATRIX // QUALIFIED SIGNALS" => "FLASHPOINT MATRIX\n\nConfigured monitoring theaters shown with current index, protocol-family activity, and source diversity. Dashes indicate no qualifying active signal in that category.",
            "GLOBAL THEATER MATRIX // QUALIFIED SIGNALS" => "GLOBAL THEATER MATRIX\n\nMonitoring scenarios are grouped by theater. Asia, Europe, and the Middle East are displayed first as the primary focus, while Africa and the Americas remain visible for global context. A theater is a monitoring lens, not a claim that escalation is occurring.",
            "STRATEGIC POSTURE / RESILIENCE" => "STRATEGIC POSTURE / RESILIENCE\n\nCompact readouts for strategic, cyber, infrastructure, and continuity-related protocol families. These are analytic classifications, not confirmations of operations.",
            "SCENARIO WATCHLIST / ACTIVITY WINDOW" => "SCENARIO WATCHLIST\n\nConfigured actors and geography used to group evidence into a monitoring lens. A scenario name is not a claim that the scenario is occurring.",
            "THEATER FOCUS / ACTIVITY WINDOW" => "THEATER FOCUS\n\nA compact summary of configured monitoring theaters. Asia, Europe, and the Middle East are primary display focus areas. The remaining theaters preserve global context without changing scoring rules or alert thresholds.",
            "REMOTE COLLECTION NODES" => "REMOTE COLLECTION NODES\n\nHealth and schedule status for configured feeds. A feed failure, delay, or small item count affects collection coverage and should not be interpreted as an absence of events.",
            "ACTIVE ALERT CENTER" => "ACTIVE ALERT CENTER\n\nUnread operational signals and assessment-rule alerts. Acknowledge or clear items after review; clearing changes alert state, not the retained source record.",
            "OPERATOR CONTROL" => "OPERATOR CONTROL\n\nLaunches scans and focused views. Each action opens a separate console or triggers a clearly labeled collection task; Ctrl-hover a button for its exact effect.",
            _ => string.IsNullOrWhiteSpace(caption) ? "CONSOLE PANEL\n\nA grouped operational display. Hold Ctrl over contained items for contextual definitions." : $"{caption}\n\nA grouped operational display. Hold Ctrl over contained items for contextual definitions."
        };
    }

    private static string ButtonHelp(string rawText)
    {
        var key = rawText.Replace("[", "").Replace("]", "").Trim().ToUpperInvariant();
        return key switch
        {
            "RUN ACTIVE SCAN" => "RUN ACTIVE SCAN\n\nFetches the configured sources, normalizes new records, evaluates the assessment, and refreshes local displays. It may take time and depends on source availability; it does not contact or control any external actor.",
            "ASSESSMENT CONSOLE" => "ASSESSMENT CONSOLE\n\nOpens the detailed analytical workspace: evidence search, scoring movement, scenario views, history, alerts, protocols, exports, and calibration tools.",
            "OPEN CONFIGURATION" => "OPEN CONFIGURATION\n\nOpens the local configuration file for review or editing. Changes take effect only after the application reloads configuration; validate syntax before restarting.",
            "DATABASE BROWSER" => "DATABASE BROWSER\n\nOpens a read-only, paged view of local articles, matches, evidence, assessments, feed logs, archives, and images. It never edits database content.",
            "MAP / ALERT CATEGORIES" => "MAP / ALERT CATEGORIES\n\nOpens the geographic theater view. Category buttons filter displayed protocol families; marker locations are regional anchors, not incidents or force positions.",
            "OPERATIONS WORKSPACE" => "OPERATIONS WORKSPACE\n\nOpens eight focused views for source coverage, scan changes, claim comparison, watchlists, review notes, data-quality flags, what-if tests, and verified backups. The main score is not altered by opening it.",
            "IGNORED NEWS / RESTORE" => "IGNORED NEWS / RESTORE\n\nOpens the active article exclusions. Search the complete list, inspect the original source, and restore an article to new assessments. The retained article and old assessment snapshots are never deleted.",
            "RESTORE SELECTED TO SCORE" => "RESTORE SELECTED TO SCORE\n\nReverses the selected article-level exclusion and recalculates the current assessment. Other articles and historical assessment snapshots are unchanged.",
            "ADD / UPDATE" => "ADD / UPDATE WATCH\n\nSaves one selected theatre, actor, or protocol interest and its minimum evidence-confidence threshold. Existing evidence is not re-alerted; future qualifying changes can create a local watchlist alert.",
            "ENABLE / DISABLE" => "ENABLE / DISABLE WATCH\n\nToggles the selected watchlist preference for future alerts. It does not delete the watch item or any past alert.",
            "REVIEW SELECTED CLAIM" or "USE SELECTED CLAIM" => "USE SELECTED CLAIM\n\nCopies the selected claim family's local ID into the analyst-review form. It does not save a judgment until Save Review is clicked.",
            "SAVE REVIEW" => "SAVE REVIEW\n\nAppends an analyst outcome and note for this event family. The source records, previous reviews and live index remain unchanged.",
            "RUN LOCAL SIMULATION" => "RUN LOCAL SIMULATION\n\nReplays stored extracted evidence at the chosen UTC time under baseline and temporary variant settings. It writes no live rule, evidence, or assessment rows.",
            "CREATE VERIFIED BACKUP" => "CREATE VERIFIED BACKUP\n\nCreates a new self-contained SQLite copy at a chosen unused path and checks its structure and required tables. It does not overwrite the running database or an existing backup.",
            "VERIFY BACKUP" => "VERIFY BACKUP\n\nOpens a selected SQLite file read-only and checks structural integrity, required tables and basic counts. It does not restore or modify the live database.",
            "TEST RESTORE TO COPY" => "TEST RESTORE TO COPY\n\nCopies a verified backup to a new file and verifies that second database. This exercises a restore path without replacing the live database.",
            "INSPECT VERIFIED BACKUP" => "INSPECT VERIFIED BACKUP\n\nOpens the last verified backup or restore-test copy in the read-only database browser.",
            "HELP / DEFINITIONS" => "HELP / DEFINITIONS\n\nOpens the searchable glossary, including detection protocol meanings and military, intelligence, cyber, logistics, and resilience terms used by the application.",
            "CLEAR ALL" => "CLEAR ALL\n\nAcknowledges every currently unread signal and rule alert in the alert center. It does not delete evidence, articles, images, assessments, or history. Newly detected items can appear after the next scan.",
            "ALL SIGNALS" => "ALL SIGNALS\n\nShows every retained signal in the main event table, subject to the normal collection and deduplication rules.",
            "CRITICAL" => "CRITICAL\n\nFilters the main event table to the highest-severity records. This is a display filter only.",
            "REFRESH MAP" => "REFRESH MAP\n\nReloads current assessment, evidence, and actionable alerts for the map view without starting a new network collection cycle.",
            "GALLERY VIEW" => "GALLERY VIEW\n\nSwitches archived images to the virtual gallery. It contains every matching local image in a single scroll range while retaining only nearby thumbnails in memory. No image is downloaded from the internet.",
            "TABLE VIEW" => "TABLE VIEW\n\nReturns archived images to the sortable, paged table view while retaining the current local filters.",
            "RELOAD GALLERY" => "RELOAD GALLERY\n\nRe-queries the complete matching local image metadata set with the current date, search, article scope, and sort settings. It cancels stale thumbnail work safely and does not download anything.",
            "REFRESH" => "REFRESH\n\nReloads the current local view using the active filters. It does not start a source scan or modify data.",
            "COPY RECORD" => "COPY RECORD\n\nCopies the selected record’s displayed raw fields to the clipboard for analysis or documentation. The database remains unchanged.",
            "FIRST" => "FIRST PAGE\n\nMoves to the first page of the full current local query.",
            "LAST" => "LAST PAGE\n\nMoves to the final page of the full current local query. The status line reports the total matching row count.",
            "GO" => "GO TO PAGE\n\nUses the adjacent page number to move directly through a large local table without repeatedly clicking Next.",
            "CLEAR FILTERS" => "CLEAR FILTERS\n\nClears text search, UTC date bounds, and manual image scope, then reloads the unmodified local dataset. It does not delete records or change alert state.",
            "PREVIOUS" or "◀ PREVIOUS" => "PREVIOUS PAGE\n\nShows the preceding page of the current read-only database query.",
            "NEXT ▶" or "NEXT 250" => "NEXT PAGE\n\nShows the next bounded page of the current query so large local tables remain responsive.",
            "REPLAY UTC" => "REPLAY UTC\n\nRecalculates the configured assessment as of the selected UTC timestamp using only records that were available by the relevant publication and first-seen cutoffs.",
            "PLAY / PAUSE" => "PLAY / PAUSE\n\nStarts or stops a controlled historical replay. It affects the replay view only and never alters retained assessment history.",
            "SAVE EVALUATION" => "SAVE EVALUATION\n\nStores a manual calibration or review outcome in the local evaluation log. It does not rewrite historical source records or scores.",
            _ when key.StartsWith("EXPORT ") => $"{key}\n\nExports the current assessment and provenance in the selected format. Full article bodies and archived image payloads are excluded by design.",
            _ when key.StartsWith("IMPORT ") => "IMPORT CSV / JSON\n\nImports supplied metadata into the local database after format and size checks. Imported records receive the current first-seen time, so replay does not treat them as historically available earlier.",
            _ => string.IsNullOrWhiteSpace(key) ? "ACTION BUTTON\n\nActivates the labeled operation. Review the label and current window before proceeding." : $"{key}\n\nActivates this labeled operation in the current view. It does not modify retained evidence unless the action explicitly says Save, Import, Acknowledge, Mute, or Snooze."
        };
    }

    private static string LabelHelp(string text)
    {
        var key = text.Trim().ToUpperInvariant();
        if (key.Contains("GMT") || key.Contains("ZULU")) return "GMT / ZULU TIME\n\nThis application treats GMT/Zulu as UTC, the zero-offset time standard used to compare activity across regions. It is separate from your local desktop time.";
        if (key.Contains("SYSTEM READY")) return "SYSTEM STATUS\n\nIndicates whether the local application is ready to run its configured collection and analysis workflow. It does not indicate the status of external systems or real-world operations.";
        if (key.Contains("NO UNREAD ALERTS")) return "ALERT STATE\n\nNo unread local signal or assessment-rule alerts are currently displayed. This is not evidence that no relevant real-world activity has occurred.";
        return $"READOUT\n\n{text}\n\nThis is a live label in the current view. Hold Ctrl over nearby controls, table headers, or terms for more specific context.";
    }

    private static string ColumnHelp(string rawHeader)
    {
        var header = rawHeader.Replace("_", " ").Trim().ToUpperInvariant();
        return header switch
        {
            "RECORD ID" or "EVENTID" or "EVENT ID" => "RECORD ID\n\nThe local identifier for a retained article, normalized evidence event, or assessment record. It supports traceability inside this application; it is not an external case number.",
            "LEVEL" or "SEVERITY" => "SEVERITY\n\nConfigured display classification for the matched protocol or alert. It prioritizes review and is not a verified real-world threat level.",
            "SCORE" or "RISK" or "IDX" => "INDEX / SCORE\n\nA descriptive calculated value in this application. It summarizes configured evidence rules and must not be read as a probability, forecast, or certainty statement.",
            "CONFIDENCE" => "CONFIDENCE\n\nAn evidence-support measure based on retained material, corroboration, recency, and configured source handling. It does not measure the completeness of real-world reporting.",
            "PUBLISHED" => "PUBLISHED\n\nThe timestamp supplied by the source for publication. Compare it with Detected or First Seen to understand collection delay.",
            "DETECTED" or "FIRST SEEN" => "DETECTED / FIRST SEEN\n\nWhen AllianceWatch first retained or normalized the record. This is distinct from the original publication time and is important for historical replay.",
            "ACTOR MATRIX" or "ACTORS" => "ACTOR MATRIX\n\nEntities extracted from the retained text. Co-mention does not prove interaction, responsibility, command, or intent.",
            "MATCHED INDICATORS" or "PROTOCOLS" => "MATCHED INDICATORS / PROTOCOLS\n\nConfigured detection rules whose text and source conditions matched this record. Matches are verification cues, not factual findings by themselves.",
            "SIGNAL / EVENT" or "TITLE" => "SIGNAL / EVENT\n\nSource title or normalized event description. Open the supporting record and source URL for full context, provenance, and qualifications.",
            "SOURCE" or "PUBLISHER" => "SOURCE\n\nThe feed or publisher associated with the retained record. Source identity contributes to provenance and corroboration handling but does not guarantee an individual claim.",
            "WT" or "WEIGHT" => "WEIGHT\n\nConfigured contribution or relevance weight used by the assessment calculation. It is an internal model parameter, not a confidence percentage.",
            "FLASHPOINT" or "SCENARIO" => "FLASHPOINT / SCENARIO\n\nA configured monitoring lens that groups relevant actors and geography. It is not an assertion that a conflict scenario is occurring.",
            "THEATER" => "THEATER\n\nA broad geographic monitoring grouping. Asia, Europe, and the Middle East are primary display focus areas; Africa and the Americas are shown for global context. Theater labels are not claims about an active area of operations.",
            "CONFIGURED FEEDS" => "CONFIGURED FEEDS\n\nThe count of enabled source feeds assigned to the selected theatre in local configuration.",
            "FRESH FEEDS" => "FRESH FEEDS\n\nEnabled feeds with a successful fetch within the greater of one hour or twice their configured polling interval. Freshness is not completeness.",
            "FAILED FEEDS" => "FAILED FEEDS\n\nAssigned feeds whose fetch history shows failures and no recent successful fetch. Inspect the feed row for the underlying error.",
            "VERIFIED ORIGINS" or "ORIGINAL ORIGINS" => "VERIFIED ORIGINS\n\nDistinct origins configured as original reporting among recent records. Syndicated copies are not counted as independent origins.",
            "CHALLENGES" => "CLAIM CHALLENGES\n\nThe count of records in this event family marked disputed, correction, or retraction. Review the reports; a flag is not an automatic factual determination.",
            "OUTCOME" => "ANALYST OUTCOME\n\nThe saved human review label for an event family. It is audit history and does not automatically change the score.",
            "TRIGGER KIND" => "ASSESSMENT TRIGGER\n\nWhy the assessment was saved: completed scan, startup, manual refresh, or import. This allows scan-to-scan comparisons to exclude other updates.",
            "FORCE" or "DIPLOMACY" or "STRATEGIC" or "RESILIENCE" => $"{header}\n\nA protocol-family column. It displays qualified active categories for this scenario; a dash means no active qualifying protocol in the current view.",
            "SRC" or "INDEPENDENT SOURCES" => "INDEPENDENT SOURCES\n\nDistinct original-reporting sources contributing after duplicate and syndication handling. More sources help corroborate a signal but do not independently prove it.",
            _ => $"{rawHeader}\n\nThis is a field in the current retained-data view. Hover a related control or consult Help / Definitions for the underlying analytical term and provenance guidance."
        };
    }
}
