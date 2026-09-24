namespace AllianceWatch;

internal sealed record GlossaryEntry(string Term, string Category, string Definition);

internal sealed class HelpForm : Form
{
    private readonly TextBox _search = new() { Dock = DockStyle.Top, Height = 34, PlaceholderText = "Search terms, protocols, acronyms, or definitions…" };
    private readonly DataGridView _grid = new();
    private readonly IReadOnlyList<GlossaryEntry> _entries;

    public HelpForm(IReadOnlyList<ProtocolDefinition> protocols)
    {
        Text = "AllianceWatch // Operator Help and Definitions";
        Size = new Size(1160, 760);
        MinimumSize = new Size(800, 540);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Void;
        ForeColor = UiTheme.Text;
        Font = UiTheme.Small;

        _entries = BuildEntries(protocols).OrderBy(entry => entry.Category).ThenBy(entry => entry.Term).ToArray();
        ConfigureGrid();

        var notice = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(10, 7, 10, 4),
            ForeColor = UiTheme.Cyan,
            Font = UiTheme.Small,
            Text = "OPERATOR HELP / GLOSSARY\r\nDefinitions describe terms used by this software; they do not validate a report or establish a real-world event. Search covers every protocol and dashboard term."
        };
        Controls.Add(_grid);
        Controls.Add(_search);
        Controls.Add(notice);
        _search.TextChanged += (_, _) => ApplyFilter();
        UiToolTips.Enable(this);
        ApplyFilter();
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.EnableHeadersVisualStyles = false;
        _grid.BackgroundColor = UiTheme.Surface;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = UiTheme.Grid;
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, SelectionBackColor = UiTheme.Raised,
            SelectionForeColor = UiTheme.CyanHot, Font = UiTheme.Small, WrapMode = DataGridViewTriState.True, Padding = new Padding(6, 5, 6, 5)
        };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.Void, ForeColor = UiTheme.Cyan, Font = UiTheme.Label,
            SelectionBackColor = UiTheme.Void, Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6)
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(GlossaryEntry.Term), HeaderText = "TERM", Width = 245, MinimumWidth = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(GlossaryEntry.Category), HeaderText = "CATEGORY", Width = 150, MinimumWidth = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(GlossaryEntry.Definition), HeaderText = "DEFINITION", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 360 });
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        _grid.DataSource = string.IsNullOrWhiteSpace(query)
            ? _entries
            : _entries.Where(entry => ($"{entry.Term} {entry.Category} {entry.Definition}").Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static IEnumerable<GlossaryEntry> BuildEntries(IReadOnlyList<ProtocolDefinition> protocols)
    {
        var entries = new List<GlossaryEntry>();
        foreach (var protocol in protocols)
        {
            entries.Add(new GlossaryEntry($"P{protocol.Id:D2} — {protocol.Name}", "Detection protocol",
                ProtocolDefinition(protocol.Id, protocol.Name)));
        }

        entries.AddRange(
        [
            new("GMT / Zulu time", "Timekeeping", "GMT and Zulu both refer to the zero-offset time standard used for cross-border coordination. This console displays GMT/Zulu as UTC; local time remains separate."),
            new("Assessment", "Dashboard", "A timestamped, append-only calculation from the evidence records available to the system at that time."),
            new("Risk index", "Dashboard", "A normalized 0–100 internal indicator derived from configured public-report protocols. It is not a probability, forecast, or statement that conflict will occur."),
            new("Confidence", "Dashboard", "A measure of support for collected evidence, weighing source quality, corroboration, contradictions, and missing companion indicators. It does not measure total real-world coverage."),
            new("Momentum", "Dashboard", "The recent direction of the internal index when enough historical assessments exist: accelerating, decelerating, or stable."),
            new("Escalation ladder", "Dashboard", "A descriptive rule classification based on corroborated active protocols. Higher states require analyst verification and are never created by a headline alone."),
            new("Flashpoint matrix", "Dashboard", "A scenario-by-category display of active protocol tags, index values, and independent-source counts. A dash means no qualifying active protocol in that category."),
            new("Scenario watchlist", "Dashboard", "A configured set of actors and geography used to group relevant evidence. It is a monitoring lens, not an assertion that the scenario is occurring."),
            new("Theater anchor", "Dashboard", "A broad geographic marker used to organize a monitoring scenario on the map. It is not an incident coordinate, unit location, force tracker, or operational boundary."),
            new("Theater hierarchy / focus", "Dashboard", "AllianceWatch groups scenarios into Asia, Europe, Middle East, Africa, North America, Central America, and South America. Asia, Europe, and the Middle East are display priorities; this changes presentation order only, not rules, thresholds, or the meaning of evidence. The Central America regional scenario covers Belize, Guatemala, Honduras, El Salvador, Nicaragua, Costa Rica, and Panama and requires two named regional actors before assigning evidence to that scenario."),
            new("Central America regional scenario", "Dashboard", "A monitoring lens for Belize, Guatemala, Honduras, El Salvador, Nicaragua, Costa Rica, and Panama. It appears on the dashboard, map, watchlists, and feed-coverage view. An article must name at least two of those countries or their configured aliases to be assigned to this regional scenario; the label does not mean a conflict is occurring. The map point is a schematic regional anchor, not an event location."),
            new("Activity window", "Dashboard", "The recent time range used for a display. It is a view filter and does not delete or override the permanent evidence record."),
            new("Virtual image gallery", "Database browser", "A single scrollable gallery that represents every image in the current local result set without creating a separate desktop control for every record. It keeps complete image metadata but decodes only nearby previews."),
            new("Thumbnail / thumbnail cache", "Database browser", "A thumbnail is a small locally generated image preview. The cache is a bounded temporary memory store for recently visible previews; older previews are released automatically and can be regenerated from the local archive."),
            new("Image metadata", "Database browser", "Descriptive fields about an archived image—such as ID, article hash, title, source URL, MIME type, position, date, and byte size—separate from the compressed image payload."),
            new("Article hash", "Database browser", "The stable local identifier for an article version. It links the article record to its archive and archived images and can be used in the database browser’s manual image scope field."),
            new("All-article archive search", "Database browser", "The main search box and Ctrl+F open a read-only search across all saved articles, including older and ignored news. The entered text is treated as a literal case-insensitive substring across article fields, linked signal and normalized-evidence fields, archive metadata, image metadata, and compressed saved article text or HTML. Searches run in the background and can be cancelled; an empty query browses all articles."),
            new("MIME type", "Database browser", "A standard file-format label used by systems, for example image/jpeg or image/png. It describes the archived image’s declared content type; it does not validate the image by itself."),
            new("Parameterized query", "Database browser", "A database query that keeps entered search text separate from query instructions. This prevents search text from changing the query structure; database-browser searches are read-only."),
            new("Result scope / page", "Database browser", "Scope is the selected subset of local data after view, search, date, and image filters. A page is one bounded portion of a large table result; the status line reports the total matching count and page position."),
            new("Actor signal link", "Dashboard", "Two actors co-mentioned in a recent detected record. It is not proof of a bilateral relationship, intent, command relationship, or coordination."),
            new("Evidence event", "Evidence", "A normalized record created from an article or feed item, including its source, timestamp, detected actors, protocols, and provenance fields."),
            new("Qualified evidence", "Evidence", "Material retained by the application after it meets configured matching and source-handling checks. Qualification is an analytic workflow step, not proof that a report is true."),
            new("Normalization", "Evidence", "Converting source material into a consistent local record format so timestamps, sources, actors, protocol tags, and other fields can be compared."),
            new("Provenance", "Evidence", "The traceable record of where an item came from and how it entered the application: source, URL, publication time, first-seen time, and processing metadata."),
            new("First-seen time", "Evidence", "When AllianceWatch first stored or normalized a record. It may be later than the source publication time and is used to avoid future knowledge leaking into historical replay."),
            new("Deduplication", "Evidence", "Grouping repeated, syndicated, or near-identical coverage so that one underlying reported event does not inflate source counts, event counts, or assessment inputs."),
            new("Event family / cluster", "Evidence", "Records assessed as describing the same underlying report or event are grouped to reduce repeated coverage from inflating the index."),
            new("Independent source", "Evidence", "A distinct verified original-reporting origin. Republishing, syndicated copies, or different websites quoting the same origin do not count as independent corroboration."),
            new("Source origin", "Evidence", "The organization or original reporting family assigned to a feed or record. Origins are used for deduplication and corroboration checks."),
            new("Source handling", "Evidence", "The configured treatment of a source stream for provenance, duplicates, class, and calculation inputs. It does not certify every claim from that source."),
            new("Source quality", "Evidence", "A configured 0–1 reliability input. It reflects source handling in this application and does not certify that any individual claim is true."),
            new("Recency / half-life", "Evidence", "The time-decay mechanism that reduces a signal’s contribution as it ages. Each protocol has its own configured half-life."),
            new("Corroboration", "Evidence", "Support from distinct original-reporting origins for the same event family. Corroboration improves support but does not prove a claim."),
            new("Contradiction / retraction", "Evidence", "Flagged wording that disputes, corrects, or retracts a claim. Such evidence is preserved and reduces its weighted contribution."),
            new("Protocol tag", "Evidence", "A numbered, versioned text-detection rule. A tag means configured wording matched; it is a prompt for verification, not a factual finding."),
            new("Collective / mutual defense", "Military and diplomatic", "An arrangement in which parties commit to assist one another if a member is attacked. Its legal scope, triggers, and obligations vary by agreement."),
            new("Integrated / combined command", "Military and diplomatic", "A structure that coordinates forces from more than one service or country under an agreed command arrangement. Co-mention alone does not establish operational control."),
            new("Joint operations planning", "Military and diplomatic", "Coordinated planning by multiple forces or organizations for potential operations, exercises, or contingencies."),
            new("Wartime logistics / prepositioning", "Military and diplomatic", "The movement, storage, and sustainment of supplies such as fuel, ammunition, equipment, and medical material; prepositioning places material in advance locations."),
            new("Base access", "Military and diplomatic", "Permission to use a facility, airfield, port, or territory. Access can be limited by purpose, duration, host approval, and legal status."),
            new("Mobilization", "Military and diplomatic", "Activating personnel, reserves, equipment, industry, or administrative systems for a higher state of readiness."),
            new("Force dispersal", "Military and diplomatic", "Spreading forces or aircraft among locations to reduce vulnerability, improve survivability, or support operations."),
            new("Strategic deterrence", "Military and diplomatic", "Measures intended to discourage an adversary from acting by communicating credible costs or consequences. It does not necessarily imply intent to fight."),
            new("NOTAM", "Military and diplomatic", "Notice to Air Missions: an aviation notice containing information essential to flight operations, including temporary airspace restrictions."),
            new("Maritime exclusion zone / blockade", "Military and diplomatic", "An exclusion zone restricts access to a defined maritime area. A blockade is a coercive restriction on access or commerce with specific legal and operational implications."),
            new("Embassy draw-down / evacuation", "Military and diplomatic", "Reduction or relocation of diplomatic staff or nationals because of a security assessment. It is a precautionary measure, not proof that hostilities are imminent."),
            new("Civil defense", "Military and diplomatic", "Government and community measures to protect civilians and maintain essential functions during emergencies, disasters, or conflict."),
            new("Martial law / emergency powers", "Military and diplomatic", "Extraordinary legal authorities declared under domestic law. Scope, safeguards, and effects differ by jurisdiction."),
            new("Munitions surge", "Military and diplomatic", "An increased movement, production, procurement, or stockpiling of ammunition and related materiel."),
            new("Command relocation", "Military and diplomatic", "Moving a headquarters or command function to a different site for continuity, security, exercise, or operational reasons."),
            new("Cyber alert", "Military and diplomatic", "An official notification of elevated cyber risk or an incident affecting systems or networks. It does not attribute responsibility by itself."),
            new("Critical infrastructure", "Military and diplomatic", "Systems essential to society or the economy, such as energy, water, health, transport, communications, and finance."),
            new("Satellite / space asset disruption", "Military and diplomatic", "Loss, degradation, interference, or attack affecting space-based services such as communications, navigation, or observation."),
            new("Ultimatum / red line", "Military and diplomatic", "A demand linked to stated consequences or a declared limit. Public rhetoric requires contextual verification before it is treated as a material escalation signal."),
            new("Nuclear posture", "Military and diplomatic", "The readiness, deployment, policy, and command arrangements for nuclear forces. Public reporting may be incomplete or deliberately ambiguous."),
            new("Export controls / capital controls", "Economic and resilience", "Government restrictions on trade in specified goods or technology, or on movement of money and financial assets."),
            new("War-risk premium", "Economic and resilience", "An additional insurance cost associated with operating in a conflict-affected area. It can reflect commercial risk assessment, not a verified military event."),
            new("Strategic industry mobilization", "Economic and resilience", "Government or industry action to increase capacity for defense-relevant production, repair, supply, or resilience."),
            new("Kinetic event", "Military and diplomatic", "An action that physically damages, destroys, or uses force against a target, as distinct from signaling, cyber activity, or political statements."),
            new("Multi-theater", "Military and diplomatic", "Involving more than one geographically distinct operational area. The term alone does not establish coordination or a single conflict."),
            new("Acknowledge / clear alert", "Operator controls", "Marks an alert as read in this local console. It does not alter the source article, evidence event, assessment history, or protocol result."),
            new("Remote collection node", "Operator controls", "A configured RSS, Atom, JSON, CSV, or watch source. Online status describes the last collection attempt, not the source’s overall availability."),
            new("Active scan", "Operator controls", "A collection cycle that checks sources according to their due time and backoff policy, then refreshes the assessment from local evidence."),
            new("Protocol ID", "Operator controls", "The stable P01–P30 identifier used to show which configured detector matched. Open the Assessment Console for patterns, exclusions, and version details."),
            new("Coverage / blind spot", "Operations workspace", "Coverage summarizes enabled feed freshness and recent reporting by theatre. A blind spot is an information gap; it does not imply calm conditions or absence of an event."),
            new("Fresh feed", "Operations workspace", "An enabled feed with a successful collection within the greater of one hour or twice its configured polling interval. This measures collection currency, not whether its reports are complete or accurate."),
            new("Verified origin", "Operations workspace", "A distinct source origin explicitly configured as original reporting in the recent theatre records. Different sites repeating one origin are not counted separately."),
            new("Since last scan", "Operations workspace", "A comparison of the latest assessment with the preceding completed scan assessment. When older history has no scan marker, the view explicitly falls back to the previous saved assessment."),
            new("Claim comparison", "Operations workspace", "A side-by-side view of records grouped into an event family, including linked corrections, disputes, and retractions. Grouping does not establish the claim as true."),
            new("Personal watchlist", "Operations workspace", "Operator-selected theatre, actor, or protocol interests. A watchlist alert requires a new or materially strengthened scored event meeting the selected minimum evidence-confidence threshold."),
            new("Analyst review / false positive", "Operations workspace", "A separate, append-only human judgment attached to an event family. Marking a false positive records a review; it does not change the original evidence or automatically change the live score."),
            new("Data-quality warning", "Operations workspace", "A diagnostic flag for source outages, timestamp anomalies, duplicate-heavy fetches, source concentration, or a large assessment jump. It asks for inspection; it is not a geopolitical alert."),
            new("What-if lab", "Operations workspace", "A local same-time replay that compares current settings with simulated normalization, convergence, one protocol severity, or one origin's source quality. No live assessment, rule, or evidence is rewritten."),
            new("Verified backup / restore copy", "Operations workspace", "A self-contained SQLite backup checked for database integrity and required AllianceWatch tables. A restore test creates and verifies a separate copy; it never replaces the running database.")
        ]);
        entries.AddRange(ExpandedTerms);
        return entries;
    }

    private static readonly GlossaryEntry[] ExpandedTerms =
    [
        new("C2 / Command and Control", "Command and control", "The exercise of authority and direction over assigned forces. It includes people, procedures, communications, information, and decision-making."),
        new("C3", "Command and control", "Command, control, and communications: the command function together with the systems used to exchange information."),
        new("C4ISR", "Command and control", "Command, control, communications, computers, intelligence, surveillance, and reconnaissance. The term describes an integrated information and decision-support enterprise."),
        new("Chain of command", "Command and control", "The formal line of authority through which orders, responsibility, and accountability flow."),
        new("Command post", "Command and control", "A location from which a commander and staff direct operations; it may be fixed, mobile, or distributed."),
        new("Command continuity", "Command and control", "Measures intended to preserve command functions if a headquarters, communications link, or leader is disrupted."),
        new("COCOM", "Command and control", "Combatant command: U.S. authority over assigned forces, generally broad and enduring within its approved scope."),
        new("OPCON", "Command and control", "Operational control: authority to organize and employ assigned forces for missions, normally narrower than combatant command."),
        new("TACON", "Command and control", "Tactical control: authority to direct detailed movement or maneuvers needed to accomplish an assigned task."),
        new("Supported / supporting command", "Command and control", "A relationship in which one command has primary responsibility for a task and another provides assistance within defined limits."),
        new("Joint Task Force (JTF)", "Command and control", "A temporary joint organization formed to accomplish a mission requiring forces from more than one service or component."),
        new("Rules of engagement (ROE)", "Command and control", "Directives defining when, where, and how forces may use force. ROE are context-specific and may not be public."),
        new("CONOPS", "Command and control", "Concept of operations: a high-level description of how an organization intends to achieve an objective."),
        new("OPLAN / OPORD", "Command and control", "An operation plan sets out planned actions; an operation order directs execution. Their existence does not by itself mean execution has begun."),
        new("Contingency plan", "Command and control", "A prepared plan for a possible future event. It is prudent planning, not evidence that the event is expected or authorized."),
        new("Battle rhythm", "Command and control", "The recurring schedule of briefings, decisions, reporting, and coordination used by a headquarters."),

        new("ISR", "Intelligence", "Intelligence, surveillance, and reconnaissance: activities that collect, process, and use information about environments, forces, and events."),
        new("OSINT", "Intelligence", "Open-source intelligence derived from publicly available material. Quality depends on provenance, corroboration, and analysis."),
        new("HUMINT", "Intelligence", "Human intelligence obtained from people. This application does not collect or manage HUMINT."),
        new("SIGINT", "Intelligence", "Signals intelligence derived from intercepted electronic signals. Public reporting about SIGINT may be incomplete or sensitive."),
        new("GEOINT", "Intelligence", "Geospatial intelligence based on imagery, maps, and location-related data."),
        new("IMINT", "Intelligence", "Imagery intelligence derived from photographs or imagery, including satellite and aerial imagery."),
        new("All-source assessment", "Intelligence", "An assessment that considers multiple intelligence disciplines and other information rather than relying on one stream."),
        new("Indications and warning", "Intelligence", "Information that may suggest a developing threat, change, or event. It is designed to prompt attention, not provide certainty."),
        new("Order of battle", "Intelligence", "A structured description of a force’s organization, units, equipment, locations, and capabilities as assessed from available information."),
        new("Attribution", "Intelligence", "The process of assessing responsibility for an action. Public claims of attribution require corroboration and may change with new evidence."),
        new("Deception", "Intelligence", "Actions intended to influence another actor’s perceptions or decisions. A deceptive indicator cannot be reliably inferred from one headline."),

        new("Deterrence", "Nuclear and strategic", "Discouraging an action by convincing an actor that its costs or risks outweigh expected benefits."),
        new("Extended deterrence", "Nuclear and strategic", "Deterrence commitments intended to protect an ally or partner, not only a state’s own territory."),
        new("Second-strike capability", "Nuclear and strategic", "A state’s ability to respond with nuclear forces after suffering an initial nuclear attack; the term relates to survivability and deterrence."),
        new("Nuclear triad", "Nuclear and strategic", "The traditional combination of land-based missiles, submarine-launched missiles, and strategic bombers."),
        new("ICBM", "Nuclear and strategic", "Intercontinental ballistic missile: a long-range ballistic missile, often discussed in relation to strategic deterrence."),
        new("SLBM", "Nuclear and strategic", "Submarine-launched ballistic missile: a ballistic missile carried and launched by a submarine."),
        new("Ballistic-missile defense", "Nuclear and strategic", "Systems intended to detect, track, and intercept ballistic missiles. Effectiveness and coverage depend on many technical and operational factors."),
        new("Nuclear command, control, and communications", "Nuclear and strategic", "The people, procedures, and systems used to maintain control of nuclear forces and communicate authorized decisions."),
        new("No-first-use", "Nuclear and strategic", "A declaratory policy stating that a state would not be the first to use nuclear weapons in a conflict. Policy wording and credibility are separate questions."),
        new("Nuclear signaling", "Nuclear and strategic", "Public or observable actions intended to communicate resolve, capability, restraint, or deterrence. It should not be read as a forecast of nuclear use."),
        new("Strategic stability", "Nuclear and strategic", "A condition in which major powers have reduced incentives for rapid escalation or a disarming first strike."),
        new("Escalation management", "Nuclear and strategic", "Efforts to limit, control, or de-escalate a crisis. It is an objective, not a guarantee."),

        new("Readiness", "Force posture", "A force’s ability to perform assigned missions, considering personnel, equipment, training, sustainment, and time available."),
        new("Force generation", "Force posture", "The process of preparing personnel and units for assigned tasks, including staffing, training, equipment, and certification."),
        new("Forward presence", "Force posture", "The routine or sustained positioning of forces outside their home territory to reassure partners, deter threats, train, or respond."),
        new("Rotational deployment", "Force posture", "A deployment in which units replace one another over time without necessarily establishing a permanent basing arrangement."),
        new("Force protection", "Force posture", "Measures to reduce vulnerability of personnel, facilities, equipment, and operations to hostile acts or hazards."),
        new("Quick reaction force (QRF)", "Force posture", "A force held at a defined readiness level to respond rapidly to a task or incident."),
        new("Reserve force", "Force posture", "Personnel or units not on full-time active duty, or a force held back for future use; meanings vary by country and context."),
        new("Surge", "Force posture", "A temporary increase in personnel, equipment, tempo, capacity, or resources above normal levels."),
        new("Exercise / drill", "Force posture", "A planned training activity used to practice procedures or evaluate readiness. Exercises can resemble operational activity and require contextual interpretation."),
        new("Interoperability", "Force posture", "The ability of forces, systems, or organizations to operate together effectively through compatible procedures, standards, communications, and logistics."),

        new("A2/AD", "Air and maritime", "Anti-access/area denial: capabilities intended to impede an opponent’s approach to, movement within, or freedom of action in an area."),
        new("ADIZ", "Air and maritime", "Air Defense Identification Zone: an area in which aircraft may be requested to identify themselves for air-defense purposes; it is not the same as sovereign airspace."),
        new("Air superiority", "Air and maritime", "A degree of control in the air that permits operations without prohibitive interference from opposing air forces."),
        new("Air defense", "Air and maritime", "Measures and systems used to detect, identify, track, and counter aerial threats."),
        new("Integrated air and missile defense", "Air and maritime", "Coordinated capabilities and command arrangements used to address air and missile threats across systems and domains."),
        new("No-fly zone", "Air and maritime", "An area in which aircraft operations are prohibited or restricted. Legal authority, enforcement, and scope differ by case."),
        new("Freedom of navigation", "Air and maritime", "The principle and practice of lawful navigation through international waters and airspace, subject to applicable law."),
        new("FONOP", "Air and maritime", "Freedom of navigation operation: an operation intended to assert or demonstrate a legal navigation position; terminology and practice vary by state."),
        new("Exclusive economic zone (EEZ)", "Air and maritime", "A maritime zone in which a coastal state has specified economic rights under the law of the sea; it is not identical to territorial waters."),
        new("Chokepoint", "Air and maritime", "A narrow or constrained route whose disruption can affect movement, shipping, supply, or military access."),
        new("Convoy", "Air and maritime", "A group of vehicles or vessels moving together, often for coordination, security, or logistical efficiency."),
        new("ASW", "Air and maritime", "Anti-submarine warfare: activities intended to detect, track, deter, or counter submarines."),
        new("Maritime domain awareness", "Air and maritime", "Understanding activities, actors, and conditions in the maritime environment that could affect security, safety, or the economy."),
        new("Port call", "Air and maritime", "A ship’s planned stop at a port for logistics, maintenance, diplomacy, crew support, or other purposes; it does not alone establish an operational intent."),

        new("Sustainment", "Logistics and medicine", "The provision of supplies, maintenance, transportation, personnel services, and other support needed to keep a force operating."),
        new("Host-nation support", "Logistics and medicine", "Civil or military assistance supplied by a host country to visiting or deployed forces, such as facilities, transport, or services."),
        new("Status of Forces Agreement (SOFA)", "Logistics and medicine", "An agreement defining the legal status of visiting foreign forces. It does not itself grant a combat mission or base access."),
        new("Lines of communication", "Logistics and medicine", "Routes and networks used to move people, supplies, information, and equipment between forces and their support base."),
        new("Strategic lift", "Logistics and medicine", "Long-range transportation of personnel or materiel, commonly by air or sea, between theaters or from home base to a theater."),
        new("Prepositioned stock", "Logistics and medicine", "Equipment or supplies stored in advance near a possible area of need to reduce response time."),
        new("Maintenance backlog", "Logistics and medicine", "Accumulated equipment maintenance that has not yet been completed; it can affect availability but does not directly indicate intent."),
        new("MEDEVAC", "Logistics and medicine", "Medical evacuation: moving ill or injured personnel to medical care. The term is often used generically and can have specific doctrinal meanings."),
        new("Mass-casualty incident", "Logistics and medicine", "An incident that overwhelms normal medical response capacity. Planning for one is a preparedness activity, not evidence it has occurred."),
        new("Industrial base", "Logistics and medicine", "The network of companies, workers, infrastructure, and supply chains able to produce or sustain defense-relevant goods and services."),

        new("DDoS", "Cyber and space", "Distributed denial of service: traffic from many systems intended to make a service unavailable. It can have criminal, political, or military contexts."),
        new("Ransomware", "Cyber and space", "Malicious software that blocks access to data or systems while demanding payment or another concession."),
        new("Zero-day vulnerability", "Cyber and space", "A software or hardware flaw that is unknown to, or not yet fixed by, its vendor at the time it is exploited or disclosed."),
        new("Cyber resilience", "Cyber and space", "The ability to prepare for, withstand, recover from, and adapt to cyber disruption."),
        new("Critical-infrastructure cyber incident", "Cyber and space", "A cyber event affecting systems important to essential services. Cause, scale, and attribution should be verified separately."),
        new("GNSS", "Cyber and space", "Global Navigation Satellite System: satellite-based positioning, navigation, and timing services, including GPS and other constellations."),
        new("GPS interference", "Cyber and space", "Jamming or spoofing that degrades or misleads satellite navigation signals. Public reports may have multiple possible causes."),
        new("Satellite communications (SATCOM)", "Cyber and space", "Communications services relayed through satellites, used for civilian, commercial, and government purposes."),
        new("Space situational awareness", "Cyber and space", "Knowledge of objects, events, and conditions in space that may affect operations or safety."),
        new("Undersea cable", "Cyber and space", "A submarine cable carrying communications or power. Damage can be accidental, environmental, or deliberate; cause requires investigation."),

        new("Continuity of government", "Civil preparedness", "Plans and capabilities intended to maintain essential government functions during severe disruption."),
        new("Continuity of operations", "Civil preparedness", "An organization’s ability to continue essential functions during and after an emergency or disruption."),
        new("Emergency management", "Civil preparedness", "Preparedness, response, recovery, and mitigation activities for disasters, incidents, and other emergencies."),
        new("Shelter-in-place", "Civil preparedness", "Guidance for people to remain in a protected location during a hazard rather than evacuate immediately."),
        new("Evacuation order", "Civil preparedness", "An official instruction or request to leave an area for safety. Local legal authority and wording differ by jurisdiction."),
        new("Critical services", "Civil preparedness", "Services needed to protect life, health, safety, and basic social functioning, such as energy, water, health, transport, and communications."),
        new("Sanctions", "Economic and resilience", "Government restrictions on specified persons, entities, goods, services, or financial activity to pursue foreign-policy or legal objectives."),
        new("Supply-chain disruption", "Economic and resilience", "A delay, shortage, or interruption in the movement of goods, components, or services. It may have many non-conflict causes."),
        new("Energy security", "Economic and resilience", "Reliable, affordable access to energy and the ability to withstand supply, infrastructure, market, or geopolitical disruptions."),
        new("Information operation", "Information environment", "Coordinated activity intended to influence information, perceptions, or decision-making. The label should not be assigned without evidence."),
        new("Disinformation", "Information environment", "False or misleading information deliberately created or spread to deceive. Intent is difficult to establish from content alone."),
        new("Misinformation", "Information environment", "False or misleading information shared without necessarily intending to deceive."),
        new("Narrative", "Information environment", "A recurring explanatory frame or storyline. In this console, narrative terms are measured separately from risk scoring."),

        new("NATO Article 4", "Alliance and legal", "A North Atlantic Treaty consultation provision invoked when a member believes its territorial integrity, political independence, or security is threatened."),
        new("NATO Article 5", "Alliance and legal", "The North Atlantic Treaty collective-defense provision concerning an armed attack against one or more members. Its application and response are political and legal decisions."),
        new("UN Security Council", "Alliance and legal", "The United Nations body with primary responsibility for international peace and security under the UN Charter."),
        new("Ceasefire", "Alliance and legal", "An agreement or arrangement to stop fighting for a period or under defined terms. A ceasefire can be local, temporary, partial, or contested."),
        new("Armistice", "Alliance and legal", "An agreement to suspend active hostilities, often while a broader political settlement remains unresolved."),
        new("Collective security", "Alliance and legal", "An arrangement in which states agree to respond collectively to threats to peace or aggression under a shared framework."),
        new("Defense pact", "Alliance and legal", "An agreement between states concerning defense cooperation or assistance. Exact commitments depend on the text and domestic law."),
        new("Mutual legal assistance", "Alliance and legal", "Cooperation between legal authorities on investigations or proceedings; it is distinct from mutual-defense obligations."),
        new("Diplomatic note", "Alliance and legal", "A formal written communication between states or diplomatic missions. It may convey positions, requests, protests, or notifications."),
        new("Demarche", "Alliance and legal", "A formal diplomatic representation or communication expressing a government’s position, concern, request, or protest.")
    ];

    private static string ProtocolDefinition(int id, string name) => id switch
    {
        1 => "Detected wording about a defense treaty or collective-defense commitment. Legal terms and actual obligations require separate review.",
        2 => "Detected wording about an integrated, combined, or joint command structure.",
        3 => "Detected wording about coordinated contingency or operational planning.",
        4 => "Detected wording about logistics, sustainment, or prepositioned military material.",
        5 => "Detected wording about reciprocal or agreed military-base access.",
        6 => "Detected wording about strategic or deepening military coordination.",
        7 => "Detected wording about activating or mobilizing reserves.",
        8 => "Detected wording about dispersing forces or aircraft.",
        9 => "Detected wording about strategic-force posture, deterrence, or a nuclear alert.",
        10 => "Detected wording about airspace restrictions, closures, or a NOTAM.",
        11 => "Detected wording about a maritime exclusion zone or blockade.",
        12 => "Detected wording about an embassy evacuation, draw-down, or evacuation of nationals.",
        13 => "Detected wording about civil-defense activation or shelters opening.",
        14 => "Detected wording about emergency powers, a national emergency, or martial law.",
        15 => "Detected wording about a large military logistics or troop-transport movement.",
        16 => "Detected wording about field hospitals, blood supplies, or medical mobilization.",
        17 => "Detected wording about munitions, fuel, or ammunition stockpiling or surge activity.",
        18 => "Detected wording about relocating a command or headquarters.",
        19 => "Detected wording about a national cyber alert or cyber emergency.",
        20 => "Detected wording about a critical-infrastructure or power-grid disruption.",
        21 => "Detected wording about satellite disruption or an anti-satellite attack.",
        22 => "Detected wording about a diplomatic breakdown, severed ties, or expelled diplomats.",
        23 => "Detected wording framed as an ultimatum, deadline, red line, or threat of measures.",
        24 => "Detected wording about nuclear safeguards, posture, or readiness.",
        25 => "Detected wording about a border closure.",
        26 => "Detected wording about capital or export controls.",
        27 => "Detected wording about war-risk insurance or shipping dislocation.",
        28 => "Detected wording about wartime footing, war economy, or a defense-production surge.",
        29 => "Detected wording about a direct state-on-state kinetic event. The application still requires corroboration and analyst verification.",
        30 => "Detected wording about synchronized activity or operations across multiple theaters.",
        _ => $"Definition for configured protocol {name}. Review its patterns, exclusions, and source requirements in the Assessment Console."
    };
}
