"""AllianceWatch: a lightweight local RSS geopolitical indicator monitor."""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import logging
import re
import sqlite3
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

import feedparser
import requests
from bs4 import BeautifulSoup


APP_NAME = "AllianceWatch"
APP_DIR = Path(__file__).resolve().parent
CONFIG_PATH = APP_DIR / "config.json"
DATABASE_PATH = APP_DIR / "alliance_watch.db"
LOG_PATH = APP_DIR / "alliance_watch.log"

# Testing/tuning controls. A score at or above the value enters that tier.
YELLOW_THRESHOLD = 4
ORANGE_THRESHOLD = 7
RED_THRESHOLD = 10
DEFAULT_MAX_POPUPS_PER_CYCLE = 5
HTTP_TIMEOUT_SECONDS = 20

CATEGORY_POINTS = {"red": 10, "orange": 7, "yellow": 4, "green": 1}

PHRASE_GROUPS: dict[str, tuple[str, ...]] = {
    "red": (
        "mutual defense", "mutual defence", "collective defense",
        "collective defence", "mutual defense treaty", "mutual defence treaty",
        "military alliance", "defense treaty", "defence treaty",
        "joint defense obligations", "joint defence obligations",
        "security guarantee", "mutual security guarantee", "collective response",
        "attack on one", "attack on either", "attack against either",
        "military assistance obligation", "treaty obligation",
        "common military command", "integrated military command",
        "combined military command",
    ),
    "orange": (
        "joint operational planning", "combined operational planning",
        "integrated command", "combined command", "joint headquarters",
        "permanent joint headquarters", "wartime planning",
        "joint contingency planning", "coordinated military response",
        "simultaneous operations", "joint strategic command",
        "integrated military operations",
    ),
    "yellow": (
        "joint logistics", "military logistics agreement", "reciprocal base access",
        "reciprocal military access", "base access agreement",
        "pre-positioned weapons", "prepositioned weapons",
        "pre-positioned ammunition", "prepositioned ammunition",
        "ammunition stockpile", "fuel stockpile", "wartime logistics",
        "repair agreement", "military rail agreement", "military port access",
        "interoperability", "strategic coordination",
        "deepening military cooperation",
    ),
    "green": (
        "non-alliance", "non alliance", "not an alliance", "non-confrontation",
        "not directed against third parties", "not targeting third parties",
        "strategic partnership", "comprehensive strategic partnership",
    ),
}

SUPPRESSION_TERMS = (
    "historical", "history of", "during the cold war", "analyst argues",
    "opinion", "commentary", "book review", "hypothetical", "simulation",
    "war game scenario",
)

ACTOR_PATTERNS: dict[str, tuple[str, ...]] = {
    "China": (r"china", r"chinese", r"beijing", r"pla"),
    "Russia": (r"russia", r"russian", r"moscow", r"kremlin"),
    "Taiwan": (r"taiwan", r"taipei"),
    "United States": (
        r"united states", r"u\.?\s*s\.?", r"us military", r"pentagon",
        r"washington",
    ),
    "NATO": (r"nato",),
    "North Korea": (r"north korea", r"dprk", r"pyongyang"),
    "Iran": (r"iran", r"iranian", r"tehran"),
    "BRICS": (r"brics",),
}

LOGGER = logging.getLogger(APP_NAME)


@dataclass(frozen=True)
class FeedConfig:
    name: str
    url: str
    weight: int


@dataclass
class MatchResult:
    article_hash: str
    feed_name: str
    title: str
    url: str
    published: str
    severity: str
    score: int
    phrases: list[str]
    actors: list[str]
    source_weight: int
    match_id: int | None = None
    popup_displayed: bool = False


def load_config(path: Path = CONFIG_PATH) -> dict[str, Any]:
    """Load and validate user-editable settings."""
    try:
        with path.open("r", encoding="utf-8") as handle:
            config = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Could not load {path}: {exc}") from exc

    feeds = config.get("feeds")
    if not isinstance(feeds, list) or not feeds:
        raise RuntimeError("config.json must contain a non-empty 'feeds' list")
    if not isinstance(config.get("poll_minutes", 10), (int, float)):
        raise RuntimeError("'poll_minutes' must be a number")
    return config


def configured_feeds(config: dict[str, Any]) -> list[FeedConfig]:
    feeds: list[FeedConfig] = []
    for number, item in enumerate(config["feeds"], start=1):
        try:
            name = str(item["name"]).strip()
            url = str(item["url"]).strip()
            weight = max(0, min(10, int(item.get("weight", 3))))
        except (KeyError, TypeError, ValueError) as exc:
            LOGGER.error("Ignoring invalid feed #%s: %s", number, exc)
            continue
        if not name or not url.lower().startswith(("http://", "https://")):
            LOGGER.error("Ignoring invalid feed #%s: name/HTTP URL required", number)
            continue
        feeds.append(FeedConfig(name, url, weight))
    return feeds


def init_database(path: Path = DATABASE_PATH) -> sqlite3.Connection:
    connection = sqlite3.connect(path)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA foreign_keys = ON")
    connection.execute("PRAGMA journal_mode = WAL")
    connection.executescript(
        """
        CREATE TABLE IF NOT EXISTS articles (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            article_hash TEXT NOT NULL UNIQUE,
            feed_name TEXT NOT NULL,
            title TEXT NOT NULL,
            url TEXT NOT NULL,
            published TEXT NOT NULL,
            summary TEXT NOT NULL,
            first_seen TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS matches (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            article_hash TEXT NOT NULL UNIQUE,
            severity TEXT NOT NULL,
            score INTEGER NOT NULL,
            matched_phrases TEXT NOT NULL,
            matched_actors TEXT NOT NULL,
            source_weight INTEGER NOT NULL,
            alerted INTEGER NOT NULL DEFAULT 0,
            detected_at TEXT NOT NULL,
            FOREIGN KEY (article_hash) REFERENCES articles(article_hash)
        );

        CREATE INDEX IF NOT EXISTS idx_matches_severity ON matches(severity, score);
        CREATE INDEX IF NOT EXISTS idx_articles_first_seen ON articles(first_seen);
        """
    )
    return connection


def setup_logging(debug: bool = False) -> None:
    LOGGER.setLevel(logging.DEBUG if debug else logging.INFO)
    LOGGER.handlers.clear()
    formatter = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
    file_handler = logging.FileHandler(LOG_PATH, encoding="utf-8")
    file_handler.setFormatter(formatter)
    LOGGER.addHandler(file_handler)
    if debug:
        console_handler = logging.StreamHandler(sys.stderr)
        console_handler.setFormatter(formatter)
        LOGGER.addHandler(console_handler)


def normalize_text(value: Any) -> str:
    """Convert untrusted RSS HTML into whitespace-normalized plain text."""
    if value is None:
        return ""
    raw = html.unescape(str(value))
    if "<" not in raw:
        return re.sub(r"\s+", " ", raw).strip()
    try:
        plain = BeautifulSoup(raw, "html.parser").get_text(" ")
    except Exception:
        plain = re.sub(r"<[^>]*>", " ", raw)
    return re.sub(r"\s+", " ", html.unescape(plain)).strip()


def _bounded_pattern(term: str) -> re.Pattern[str]:
    escaped = re.escape(term).replace(r"\ ", r"\s+")
    return re.compile(rf"(?<!\w){escaped}(?!\w)", re.IGNORECASE)


def detect_indicators(text: str) -> dict[str, list[str]]:
    found: dict[str, list[str]] = {}
    for category, phrases in PHRASE_GROUPS.items():
        matches = [phrase for phrase in phrases if _bounded_pattern(phrase).search(text)]
        if matches:
            # Longest first makes overlapping results easier to read.
            found[category] = sorted(set(matches), key=lambda item: (-len(item), item))
    return found


def detect_actors(text: str) -> list[str]:
    actors: list[str] = []
    for actor, patterns in ACTOR_PATTERNS.items():
        if any(re.search(rf"(?<!\w)(?:{pattern})(?!\w)", text, re.I) for pattern in patterns):
            actors.append(actor)
    return actors


def actor_combination_score(actors: Iterable[str]) -> int:
    """Score the single most important actor combination; edit rules here."""
    present = set(actors)
    rules: tuple[tuple[frozenset[str], int], ...] = (
        (frozenset(("China", "Russia", "Taiwan", "United States")), 5),
        (frozenset(("China", "Russia", "Taiwan")), 4),
        (frozenset(("China", "Russia", "NATO")), 4),
        (frozenset(("China", "Russia", "North Korea")), 4),
        (frozenset(("China", "Russia", "United States")), 3),
        (frozenset(("China", "Russia")), 2),
        (frozenset(("China", "Taiwan")), 2),
        (frozenset(("Russia", "NATO")), 2),
    )
    return max((points for required, points in rules if required <= present), default=0)


def calculate_score(
    indicators: dict[str, list[str]], actors: list[str], source_weight: int, text: str
) -> int:
    """Combine indicator strength, actors, source credibility, and context."""
    if not indicators:
        return 0  # Source weight or actors alone can never create an alert.

    score = max(CATEGORY_POINTS[category] for category in indicators)
    score += actor_combination_score(actors)
    score += round(max(0, min(10, source_weight)) * 0.3)

    red = bool(indicators.get("red"))
    orange = bool(indicators.get("orange"))
    yellow = bool(indicators.get("yellow"))
    lower_phrases = {phrase for phrases in indicators.values() for phrase in phrases}

    if red and orange:
        score += 4
    if yellow and orange:
        score += 3
    if red and "Taiwan" in actors:
        score += 3
    if red and "NATO" in actors:
        score += 3

    joint_command_terms = {
        "integrated command", "combined command", "joint headquarters",
        "permanent joint headquarters", "joint strategic command",
        "common military command", "integrated military command",
        "combined military command",
    }
    if {"China", "Russia", "North Korea"} <= set(actors) and lower_phrases & joint_command_terms:
        score += 4

    if any(_bounded_pattern(term).search(text) for term in SUPPRESSION_TERMS):
        score -= 3
    return max(0, score)


def classify_severity(score: int) -> str:
    if score >= RED_THRESHOLD:
        return "RED"
    if score >= ORANGE_THRESHOLD:
        return "ORANGE"
    if score >= YELLOW_THRESHOLD:
        return "YELLOW"
    return "LOG ONLY"


def fetch_feed(feed: FeedConfig) -> Any:
    headers = {
        "User-Agent": "AllianceWatch/1.0 (local RSS monitor)",
        "Accept": "application/rss+xml, application/atom+xml, application/xml, text/xml, */*",
    }
    response = requests.get(feed.url, headers=headers, timeout=HTTP_TIMEOUT_SECONDS)
    response.raise_for_status()
    parsed = feedparser.parse(response.content)
    if parsed.bozo and not parsed.entries:
        raise ValueError(f"malformed RSS: {parsed.bozo_exception}")
    if parsed.bozo:
        LOGGER.warning("feed=%r parse warning=%s", feed.name, parsed.bozo_exception)
    return parsed


def article_identifier(feed_name: str, entry: Any, title: str, url: str) -> str:
    guid = normalize_text(entry.get("id") or entry.get("guid"))
    if guid:
        source = f"guid\0{feed_name}\0{guid}"
    else:
        source = f"fallback\0{feed_name}\0{url}\0{title}"
    return hashlib.sha256(source.encode("utf-8", errors="replace")).hexdigest()


def published_value(entry: Any) -> str:
    parsed = entry.get("published_parsed") or entry.get("updated_parsed")
    if parsed:
        try:
            return datetime(*parsed[:6], tzinfo=timezone.utc).isoformat(timespec="seconds")
        except (TypeError, ValueError, OverflowError):
            pass
    return normalize_text(entry.get("published") or entry.get("updated")) or "Unknown"


def save_article(
    connection: sqlite3.Connection,
    article_hash: str,
    feed_name: str,
    title: str,
    url: str,
    published: str,
    summary: str,
) -> bool:
    cursor = connection.execute(
        """
        INSERT OR IGNORE INTO articles
            (article_hash, feed_name, title, url, published, summary, first_seen)
        VALUES (?, ?, ?, ?, ?, ?, ?)
        """,
        (article_hash, feed_name, title, url, published, summary, now_iso()),
    )
    connection.commit()
    return cursor.rowcount == 1


def save_match(connection: sqlite3.Connection, result: MatchResult) -> int:
    cursor = connection.execute(
        """
        INSERT INTO matches
            (article_hash, severity, score, matched_phrases, matched_actors,
             source_weight, alerted, detected_at)
        VALUES (?, ?, ?, ?, ?, ?, 0, ?)
        """,
        (
            result.article_hash, result.severity, result.score,
            json.dumps(result.phrases, ensure_ascii=False),
            json.dumps(result.actors, ensure_ascii=False),
            result.source_weight, now_iso(),
        ),
    )
    connection.commit()
    return int(cursor.lastrowid)


def mark_alerted(connection: sqlite3.Connection, match_id: int) -> None:
    connection.execute("UPDATE matches SET alerted = 1 WHERE id = ?", (match_id,))
    connection.commit()


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def format_alert(result: MatchResult) -> str:
    phrases = "\n".join(f"- {item}" for item in result.phrases) or "- None"
    actors = "\n".join(f"- {item}" for item in result.actors) or "- None"
    return (
        f"{result.severity} geopolitical indicator detected\n\n"
        f"Score: {result.score}\nSource: {result.feed_name}\n\n"
        f"Title:\n{result.title}\n\nMatched indicators:\n{phrases}\n\n"
        f"Actors:\n{actors}\n\nPublished:\n{result.published}\n\nURL:\n{result.url}"
    )


def show_alert(title: str, message: str) -> None:
    """Display a modal alert whose text can be selected and copied."""
    import tkinter as tk
    from tkinter import ttk

    root = tk.Tk()
    root.withdraw()
    dialog = tk.Toplevel(root)
    dialog.title(title)
    dialog.transient(root)
    dialog.resizable(True, True)
    dialog.minsize(480, 360)

    screen_width = dialog.winfo_screenwidth()
    screen_height = dialog.winfo_screenheight()
    width = min(720, max(480, screen_width - 100))
    height = min(650, max(360, screen_height - 140))
    left = max(0, (screen_width - width) // 2)
    top = max(0, (screen_height - height) // 2)
    dialog.geometry(f"{width}x{height}+{left}+{top}")

    outer = ttk.Frame(dialog, padding=14)
    outer.pack(fill="both", expand=True)
    text_frame = ttk.Frame(outer)
    text_frame.pack(fill="both", expand=True)

    alert_text = tk.Text(
        text_frame,
        wrap="word",
        font=("Segoe UI", 10),
        padx=10,
        pady=10,
        relief="solid",
        borderwidth=1,
        cursor="ibeam",
    )
    scrollbar = ttk.Scrollbar(text_frame, orient="vertical", command=alert_text.yview)
    alert_text.configure(yscrollcommand=scrollbar.set)
    alert_text.pack(side="left", fill="both", expand=True)
    scrollbar.pack(side="right", fill="y")
    alert_text.insert("1.0", message)
    alert_text.configure(state="disabled")

    def copy_selection(_event: object | None = None) -> str:
        ranges = alert_text.tag_ranges("sel")
        if ranges:
            root.clipboard_clear()
            root.clipboard_append(alert_text.get(ranges[0], ranges[1]))
            root.update_idletasks()
        return "break"

    def select_all(_event: object | None = None) -> str:
        alert_text.tag_add("sel", "1.0", "end-1c")
        alert_text.mark_set("insert", "1.0")
        alert_text.see("1.0")
        return "break"

    def copy_all() -> None:
        root.clipboard_clear()
        root.clipboard_append(message)
        root.update_idletasks()

    def close_dialog(_event: object | None = None) -> str:
        dialog.destroy()
        return "break"

    alert_text.bind("<Control-c>", copy_selection)
    alert_text.bind("<Control-C>", copy_selection)
    alert_text.bind("<Control-a>", select_all)
    alert_text.bind("<Control-A>", select_all)
    dialog.bind("<Escape>", close_dialog)

    buttons = ttk.Frame(outer, padding=(0, 12, 0, 0))
    buttons.pack(fill="x")
    ttk.Button(buttons, text="Copy All", command=copy_all).pack(side="left")
    ok_button = ttk.Button(buttons, text="OK", command=dialog.destroy)
    ok_button.pack(side="right")

    dialog.protocol("WM_DELETE_WINDOW", dialog.destroy)
    dialog.grab_set()
    dialog.lift()
    dialog.attributes("-topmost", True)
    dialog.after(250, lambda: dialog.attributes("-topmost", False))
    alert_text.focus_set()

    try:
        root.wait_window(dialog)
    finally:
        root.destroy()


def log_result(result: MatchResult) -> None:
    LOGGER.info(
        "feed=%r title=%r score=%d severity=%s matched_phrases=%s "
        "matched_actors=%s popup_displayed=%s",
        result.feed_name, result.title, result.score, result.severity,
        result.phrases, result.actors, result.popup_displayed,
    )


def _entry_text(entry: Any) -> tuple[str, str, str]:
    title = normalize_text(entry.get("title")) or "(untitled article)"
    summary = normalize_text(entry.get("summary"))
    description = normalize_text(entry.get("description"))
    combined = normalize_text(" ".join((title, summary, description)))
    stored_summary = summary if summary else description
    return title, stored_summary, combined


def check_feeds(
    config: dict[str, Any], connection: sqlite3.Connection
) -> tuple[int, int]:
    feeds = configured_feeds(config)
    print(f"[{datetime.now():%H:%M:%S}] Checking {len(feeds)} RSS feeds...")
    new_articles = 0
    new_alerts: list[MatchResult] = []
    log_only_results: list[MatchResult] = []

    for feed in feeds:
        try:
            parsed = fetch_feed(feed)
            print(f"[{datetime.now():%H:%M:%S}] {feed.name}: {len(parsed.entries)} entries")
        except Exception as exc:
            print(f"[{datetime.now():%H:%M:%S}] {feed.name}: ERROR ({exc})")
            LOGGER.error("feed=%r fetch failed: %s", feed.name, exc, exc_info=LOGGER.isEnabledFor(logging.DEBUG))
            continue

        for entry in parsed.entries:
            try:
                title, summary, combined = _entry_text(entry)
                url = normalize_text(entry.get("link"))
                published = published_value(entry)
                article_hash = article_identifier(feed.name, entry, title, url)
                if not save_article(
                    connection, article_hash, feed.name, title, url, published, summary
                ):
                    continue
                new_articles += 1

                indicators = detect_indicators(combined)
                if not indicators:
                    continue
                phrases = [phrase for group in PHRASE_GROUPS for phrase in indicators.get(group, [])]
                actors = detect_actors(combined)
                score = calculate_score(indicators, actors, feed.weight, combined)
                severity = classify_severity(score)
                result = MatchResult(
                    article_hash, feed.name, title, url, published, severity,
                    score, phrases, actors, feed.weight,
                )
                result.match_id = save_match(connection, result)
                if severity == "LOG ONLY":
                    log_only_results.append(result)
                else:
                    new_alerts.append(result)
                    print(f"\n[{severity}] score={score} {title}")
            except Exception as exc:
                LOGGER.error(
                    "feed=%r article processing failed: %s", feed.name, exc,
                    exc_info=LOGGER.isEnabledFor(logging.DEBUG),
                )

    for result in log_only_results:
        log_result(result)

    rank = {"RED": 3, "ORANGE": 2, "YELLOW": 1}
    new_alerts.sort(key=lambda item: (rank[item.severity], item.score), reverse=True)
    popup_limit = max(0, int(config.get("max_popups_per_cycle", DEFAULT_MAX_POPUPS_PER_CYCLE)))
    for index, result in enumerate(new_alerts):
        if index < popup_limit:
            try:
                show_alert(f"{APP_NAME} - {result.severity} Alert", format_alert(result))
                result.popup_displayed = True
                if result.match_id is not None:
                    mark_alerted(connection, result.match_id)
            except Exception as exc:
                LOGGER.error("Could not display popup for %r: %s", result.title, exc)
        log_result(result)

    print(f"\nCycle complete.\n{len(new_alerts)} alerts.\n{new_articles} new articles.")
    return len(new_alerts), new_articles


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--once", action="store_true", help="poll all feeds once and exit")
    mode.add_argument("--test-alert", action="store_true", help="display a test popup and exit")
    parser.add_argument("--debug", action="store_true", help="also write detailed errors to the console")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    setup_logging(args.debug)
    if args.test_alert:
        show_alert(f"{APP_NAME} - Test", "AllianceWatch test successful.")
        return 0

    try:
        config = load_config()
        connection = init_database()
    except Exception as exc:
        LOGGER.critical("Startup failed: %s", exc, exc_info=args.debug)
        print(f"AllianceWatch could not start: {exc}", file=sys.stderr)
        return 1

    try:
        if args.once:
            check_feeds(config, connection)
            return 0

        poll_seconds = max(1.0, float(config.get("poll_minutes", 10))) * 60
        print(f"{APP_NAME} is running. Press Ctrl+C to stop.")
        while True:
            check_feeds(config, connection)
            print(f"Next check in {poll_seconds / 60:g} minutes.")
            time.sleep(poll_seconds)
    except KeyboardInterrupt:
        print("\nAllianceWatch stopped.")
        LOGGER.info("Stopped by user")
        return 0
    finally:
        connection.close()


if __name__ == "__main__":
    raise SystemExit(main())
