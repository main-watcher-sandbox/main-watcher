#!/usr/bin/env python3
"""Check that architecture artifacts are owned, reviewed, and confirmed.

Architecture documentation fails by drifting, not by being wrong on day one. This checks
the mechanical preconditions for not drifting: someone owns each artifact, each has a
review date or trigger, transitional decisions have not quietly expired, and generated
content marked [unconfirmed] has actually been confirmed by a human.

Usage:
    python3 check_freshness.py docs/architecture/
    python3 check_freshness.py docs/architecture/ --warn-days 30
    python3 check_freshness.py docs/architecture/ --today 2027-01-01   # for testing

Exit code 1 when something is overdue, unowned, or still unconfirmed.
"""

import argparse
import subprocess
import sys
from datetime import date, datetime, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from _common import (ASSUMPTION, OPEN_ITEM, REQUIRED_FIELDS, UNCONFIRMED,  # noqa: E402
                     collect_artifacts)


def parse_date(value):
    if not value:
        return None
    for fmt in ("%Y-%m-%d", "%d/%m/%Y", "%Y/%m/%d"):
        try:
            return datetime.strptime(str(value).strip(), fmt).date()
        except ValueError:
            continue
    return None


def git_last_modified(path):
    try:
        out = subprocess.run(
            ["git", "log", "-1", "--format=%cs", "--", str(path)],
            capture_output=True, text=True, cwd=path.parent, timeout=10,
        )
        return parse_date(out.stdout.strip())
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser(description="Artifact ownership and freshness checks.")
    ap.add_argument("path", help="Architecture directory or file")
    ap.add_argument("--warn-days", type=int, default=30,
                    help="Warn this many days before a review falls due (default 30)")
    ap.add_argument("--today", help="Override today's date (YYYY-MM-DD), for testing")
    ap.add_argument("--no-git", action="store_true", help="Skip git modification checks")
    args = ap.parse_args()

    today = parse_date(args.today) or date.today()
    # Generated files have no human owner and no review cadence by design.
    artifacts = [a for a in collect_artifacts(args.path)
                 if a.get("type") != "generated"]
    if not artifacts:
        print(f"No markdown found under {args.path}", file=sys.stderr)
        return 2

    overdue, unowned, due_soon, unconfirmed, undated, stale, transitional = [], [], [], [], [], [], []

    for a in artifacts:
        if not a.meta:
            unowned.append(f"{a.name}: no frontmatter at all")
            continue

        missing = [f for f in REQUIRED_FIELDS if not a.get(f)]
        if missing:
            unowned.append(f"{a.name}: missing {', '.join(missing)}")

        review_by = parse_date(a.get("review_by"))
        trigger = a.get("review_trigger")
        reviewed = parse_date(a.get("reviewed"))

        if not review_by and not trigger:
            undated.append(f"{a.name}: no review_by date and no review_trigger — "
                           f"nothing will ever prompt a re-read")
        elif review_by:
            if review_by < today:
                days = (today - review_by).days
                overdue.append(f"{a.name}: review was due {review_by} ({days} days ago), "
                               f"owner {a.get('owner') or 'UNASSIGNED'}")
            elif review_by - timedelta(days=args.warn_days) <= today:
                due_soon.append(f"{a.name}: review due {review_by}")

        if a.get("state") == "transition":
            detail = trigger or "no trigger recorded"
            status = "OVERDUE" if (review_by and review_by < today) else "active"
            transitional.append(f"{a.name} [{status}]: {detail}")

        n_unconf = len(UNCONFIRMED.findall(a.raw))
        if n_unconf:
            unconfirmed.append(f"{a.name}: {n_unconf} [unconfirmed] item(s) awaiting a human")
        if a.get("confidence") in ("assumed", "unverified") and a.get("status") in ("agreed", "accepted"):
            unconfirmed.append(f"{a.name}: status '{a.get('status')}' but confidence "
                               f"'{a.get('confidence')}' — agreed on unverified information?")

        if not args.no_git and reviewed:
            modified = git_last_modified(a.path)
            if modified and modified > reviewed + timedelta(days=1):
                stale.append(f"{a.name}: changed {modified} but reviewed field still says "
                             f"{reviewed}")

    def block(items, title):
        if items:
            print(f"\n{title} ({len(items)}):")
            for i in items:
                print(f"  {i}")
        return len(items)

    n_assume = sum(len(ASSUMPTION.findall(a.raw)) for a in artifacts)
    n_open = sum(len(OPEN_ITEM.findall(a.raw)) for a in artifacts)
    print(f"Checked {len(artifacts)} artifact(s) as of {today}")
    print(f"Tagged in prose: {n_assume} [assumption], {n_open} [open]")

    problems = 0
    problems += block(unowned, "UNOWNED OR INCOMPLETE METADATA")
    problems += block(overdue, "REVIEW OVERDUE")
    problems += block(undated, "NO REVIEW DATE OR TRIGGER")
    problems += block(unconfirmed, "AWAITING HUMAN CONFIRMATION")
    problems += block(stale, "CHANGED SINCE LAST REVIEW")
    block(due_soon, "DUE SOON (not a failure)")
    block(transitional, "TRANSITIONAL DECISIONS (expected to expire)")

    if not problems:
        print("\nEvery artifact is owned, dated, and confirmed.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
