#!/usr/bin/env python3
"""Read-only diagnostics for contradictory or weak PersonalRSS feedback signals."""

from __future__ import annotations

import html
import importlib.util
import json
import re
import sqlite3
import sys
from collections import Counter, defaultdict
from pathlib import Path
from urllib.parse import parse_qsl, unquote, urlencode, urlsplit, urlunsplit


ROOT = Path(__file__).resolve().parents[1]
EVALUATOR_PATH = ROOT / "scripts" / "evaluate-local-ai-models.py"
spec = importlib.util.spec_from_file_location("local_ai_evaluator", EVALUATOR_PATH)
evaluator = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = evaluator
assert spec.loader is not None
spec.loader.exec_module(evaluator)

TITLE_TOKEN_RE = re.compile(r"[\w+#.\-]+", re.UNICODE)
TRACKING = {"fbclid", "gclid", "mc_cid", "mc_eid", "ref", "source"}


def normalized_title(value: str) -> str:
    return " ".join(TITLE_TOKEN_RE.findall(html.unescape(value).lower()))


def title_tokens(value: str) -> set[str]:
    return set(TITLE_TOKEN_RE.findall(html.unescape(value).lower()))


def canonical_url(value: str) -> str | None:
    try:
        parsed = urlsplit(value)
    except ValueError:
        return None
    if parsed.scheme.lower() not in {"http", "https"} or not parsed.hostname:
        return None
    host = parsed.hostname.lower()
    if host.startswith("www."):
        host = host[4:]
    path = unquote(parsed.path).rstrip("/").lower() or "/"
    query = sorted(
        (name.lower(), value)
        for name, value in parse_qsl(parsed.query, keep_blank_values=True)
        if not name.lower().startswith("utm_") and name.lower() not in TRACKING
    )
    return urlunsplit(("", host, path, urlencode(query), ""))[2:]


def same_story(left, right) -> bool:
    left_url, right_url = canonical_url(left.link), canonical_url(right.link)
    if left_url and left_url == right_url:
        return True
    left_title, right_title = normalized_title(left.title), normalized_title(right.title)
    if len(left_title) >= 12 and left_title == right_title:
        return True
    left_tokens, right_tokens = title_tokens(left.title), title_tokens(right.title)
    if len(left_tokens) < 5 or len(right_tokens) < 5:
        return False
    return len(left_tokens & right_tokens) / len(left_tokens | right_tokens) >= 0.85


def duplicate_diagnostics(articles) -> dict:
    parents = list(range(len(articles)))

    def find(value: int) -> int:
        while parents[value] != value:
            parents[value] = parents[parents[value]]
            value = parents[value]
        return value

    def union(left: int, right: int) -> None:
        left, right = find(left), find(right)
        if left != right:
            parents[right] = left

    for left in range(len(articles)):
        for right in range(left + 1, len(articles)):
            if same_story(articles[left], articles[right]):
                union(left, right)
    groups = defaultdict(list)
    for index, article in enumerate(articles):
        groups[find(index)].append(article)
    duplicates = [members for members in groups.values() if len(members) > 1]
    conflicts = [members for members in duplicates if len({member.label for member in members}) > 1]
    return {
        "duplicate_clusters": len(duplicates),
        "articles_in_duplicate_clusters": sum(map(len, duplicates)),
        "conflicting_duplicate_clusters": len(conflicts),
        "articles_in_conflicting_duplicate_clusters": sum(map(len, conflicts)),
    }


def prompt_signal_diagnostics(training, testing) -> dict:
    positive = [(article, evaluator.tokens(article)) for article in training if article.label == "positive"]
    negative = [(article, evaluator.tokens(article)) for article in training if article.label == "negative"]
    tied, low_signal, nearest_correct = 0, 0, 0
    margins = []
    for candidate in testing:
        candidate_tokens = evaluator.tokens(candidate)
        positive_score = max(evaluator.similarity(candidate_tokens, item_tokens) for _, item_tokens in positive)
        negative_score = max(evaluator.similarity(candidate_tokens, item_tokens) for _, item_tokens in negative)
        margin = abs(positive_score - negative_score)
        margins.append(margin)
        tied += margin < 0.05
        low_signal += max(positive_score, negative_score) < 0.10
        nearest_correct += (positive_score >= negative_score) == (candidate.label == "positive")
    ordered = sorted(margins)
    return {
        "held_out_samples": len(testing),
        "nearest_example_label_accuracy": nearest_correct / len(testing),
        "positive_negative_similarity_ties_under_0.05": tied,
        "tie_rate": tied / len(testing),
        "low_signal_under_0.10": low_signal,
        "low_signal_rate": low_signal / len(testing),
        "median_similarity_margin": ordered[len(ordered) // 2],
        "calibrated_citation_cutoffs": citation_cutoffs(training, testing),
    }


def citation_cutoffs(training, testing) -> dict:
    result = {}
    for kind in (-2, -1, 1, 2):
        pool = [(article, evaluator.tokens(article)) for article in training if article.kind == kind]
        observations = []
        for candidate in testing:
            candidate_tokens = evaluator.tokens(candidate)
            nearest = max((evaluator.similarity(candidate_tokens, item_tokens) for _, item_tokens in pool), default=0)
            observations.append((nearest, (candidate.kind > 0) == (kind > 0)))
        correct = count = 0
        best = None
        for similarity_value in sorted({value for value, _ in observations}, reverse=True):
            group = [correct_value for value, correct_value in observations if value == similarity_value]
            correct += sum(group)
            count += len(group)
            precision = correct / count
            if count >= 8 and precision >= 0.90:
                best = {"minimum_similarity": similarity_value, "precision": precision, "predictions": count}
        result[str(kind)] = best
    return result


def mixed_metadata_diagnostics(db_path: Path) -> dict:
    uri = f"file:{db_path.resolve().as_posix()}?mode=ro"
    with sqlite3.connect(uri, uri=True) as connection:
        rows = connection.execute(
            """
            SELECT COALESCE(NULLIF(TRIM(a.Author), ''), '(none)'), a.FeedSourceId,
                   COALESCE(fs.Name, '(unknown)'), f.Kind
            FROM Feedback f
            JOIN Articles a ON a.Id = f.ArticleId
            LEFT JOIN Feeds fs ON fs.Id = a.FeedSourceId
            """
        ).fetchall()
    authors, feeds = defaultdict(list), defaultdict(list)
    for author, feed_id, feed_name, kind in rows:
        authors[author.casefold()].append(kind)
        feeds[(feed_id, feed_name)].append(kind)

    def summarize(groups, minimum: int) -> dict:
        eligible = [values for values in groups.values() if len(values) >= minimum]
        mixed = [values for values in eligible if any(value > 0 for value in values) and any(value < 0 for value in values)]
        return {
            "groups_with_at_least_n_votes": len(eligible),
            "mixed_direction_groups": len(mixed),
            "mixed_direction_rate": len(mixed) / len(eligible) if eligible else 0,
            "votes_in_mixed_groups": sum(map(len, mixed)),
        }

    return {"authors_minimum_3": summarize(authors, 3), "feeds_minimum_5": summarize(feeds, 5)}


def main() -> int:
    db_path = ROOT / "src" / "PersonalRSS.Web" / "data" / "personalrss.db"
    articles = evaluator.load_feedback(db_path)
    training, testing = evaluator.split_feedback(articles, 0.20, "personalrss-local-ai-v1")
    report = {
        "feedback_samples": len(articles),
        "directions": dict(Counter(article.label for article in articles)),
        "duplicates": duplicate_diagnostics(articles),
        "prompt_signal": prompt_signal_diagnostics(training, testing),
        "metadata_mixing": mixed_metadata_diagnostics(db_path),
    }
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
