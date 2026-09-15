#!/usr/bin/env python3
"""Detect contradictions and drift across architecture artifacts.

Documents go wrong quietly: a component gets renamed in one diagram, a decision is
superseded but still cited, two documents give the same component different technologies.
Each half reads fine on its own, which is why reading top to bottom rarely catches these.

Usage:
    python3 check_consistency.py docs/architecture/
    python3 check_consistency.py docs/architecture/ --strict   # name drift fails the run
    python3 check_consistency.py docs/architecture/ --json report.json   # for CI

Exit code 1 when a definite problem is found (2 for usage errors). Name-drift warnings are
advisory unless --strict, because near-identical names are sometimes legitimately different
things.
"""

import argparse
import difflib
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from _common import (ADR_PATTERN, Artifact, VALID_CONFIDENCE, VALID_STATE,  # noqa: E402
                     VALID_STATUS, collect_artifacts, norm)

# "Component | ... | Technology" style rows, plus [Tech] hints inside diagram labels
TABLE_ROW = re.compile(r"^\|(.+)\|\s*$")
HEADING = re.compile(r"^#{2,4}\s+(.+?)\s*#*$", re.MULTILINE)
OPTION_HEADING = re.compile(r"^#{3,4}\s+Option\s+\w+", re.MULTILINE | re.IGNORECASE)
ADR_REQUIRED_SECTIONS = ("context", "decision", "consequences", "revisit")
TECH_HINT = re.compile(r"([A-Z][\w .+#-]{2,40}?)\s*<br\s*/?>\s*\[([^\]]+)\]")

KNOWN_TECH = re.compile(
    r"\b(postgres(?:ql)?|mysql|mariadb|sqlite|mongodb|dynamodb|cassandra|redis|memcached|"
    r"kafka|rabbitmq|sqs|sns|servicebus|pubsub|elasticsearch|opensearch|"
    r"kubernetes|k8s|ecs|fargate|app service|lambda|azure functions|cloud run|"
    r"react|vue|angular|svelte|next\.js|nuxt|django|flask|fastapi|rails|spring|"
    r"\.net|node\.js|express|nestjs)\b", re.IGNORECASE)


def extract_table_components(text):
    """First-column values of tables whose header names components or containers."""
    components, in_table = [], False
    for line in text.splitlines():
        m = TABLE_ROW.match(line)
        if not m:
            in_table = False
            continue
        cells = [c.strip() for c in m.group(1).split("|")]
        first = cells[0].strip("*` ")
        if re.match(r"^-{2,}|^:?-+:?$", first):
            continue
        if first.lower() in ("container", "component", "service", "module", "element"):
            in_table = True
            continue
        if in_table and first and not first.startswith("["):
            components.append(re.sub(r"[*`]", "", first))
    return components


def report(findings, title):
    if not findings:
        return 0
    print(f"\n{title} ({len(findings)}):")
    for f in findings:
        print(f"  {f}")
    return len(findings)


def main():
    ap = argparse.ArgumentParser(description="Cross-artifact consistency checks.")
    ap.add_argument("path", help="Architecture directory or file")
    ap.add_argument("--strict", action="store_true",
                    help="Treat name-drift warnings as failures")
    ap.add_argument("--similarity", type=float, default=0.82,
                    help="Name-drift sensitivity, 0-1 (default 0.82)")
    ap.add_argument("--json", metavar="PATH",
                    help="Also write a machine-readable report here, for CI")
    args = ap.parse_args()

    # Generated files have no human owner and no review cadence by design.
    artifacts = [a for a in collect_artifacts(args.path)
                 if a.get("type") != "generated"]
    if not artifacts:
        print(f"No markdown found under {args.path}", file=sys.stderr)
        return 2

    errors, warnings = [], []

    # --- 1. Frontmatter vocabulary -------------------------------------------------
    for a in artifacts:
        if not a.meta:
            warnings.append(f"{a.name}: no frontmatter — no owner, status or review date")
            continue
        for field, valid in (("status", VALID_STATUS), ("state", VALID_STATE),
                             ("confidence", VALID_CONFIDENCE)):
            v = a.get(field)
            if v and v not in valid:
                errors.append(f"{a.name}: {field} '{v}' is not one of {sorted(valid)}")

    # --- 2. ADR references resolve, and superseded ADRs are not cited as current ----
    adr_files, superseded, adr_status = {}, {}, {}
    for a in artifacts:
        if a.get("type") == "adr" or ADR_PATTERN.match(a.path.stem.upper()):
            m = ADR_PATTERN.search(a.id) or ADR_PATTERN.search(a.path.stem.upper())
            if m:
                adr_files[m.group(0)] = a
                adr_status[m.group(0)] = a.get("status", "unknown")
                sup = a.get("supersedes")
                if sup:
                    for s in (sup if isinstance(sup, list) else [sup]):
                        s = s.strip()
                        if s:
                            superseded[s] = m.group(0)

    for a in artifacts:
        cited = set(ADR_PATTERN.findall(a.raw))
        own = ADR_PATTERN.search(a.id)
        for adr in sorted(cited):
            if own and adr == own.group(0):
                continue
            if adr not in adr_files:
                errors.append(f"{a.name}: cites {adr}, which has no artifact")
            elif adr_status.get(adr) in ("superseded", "deprecated") and a.get("type") != "adr":
                replacement = superseded.get(adr, "a newer ADR")
                errors.append(
                    f"{a.name}: cites {adr}, which is {adr_status[adr]} (see {replacement})")

    for old, new in superseded.items():
        if old not in adr_files:
            warnings.append(f"{new} supersedes {old}, which was not found")
        elif adr_status.get(old) not in ("superseded", "deprecated"):
            errors.append(
                f"{old} is superseded by {new} but its own status is "
                f"'{adr_status.get(old)}' — update it")

    # --- 3. Element naming drift across artifacts ----------------------------------
    labels = defaultdict(set)          # normalised -> {display forms}
    where = defaultdict(set)           # normalised -> {files}
    for a in artifacts:
        for lab in a.diagram_labels():
            labels[norm(lab)].add(lab)
            where[norm(lab)].add(a.name)

    keys = sorted(labels)
    for i, k in enumerate(keys):
        for other in keys[i + 1:]:
            if k == other or not k or not other:
                continue
            ratio = difflib.SequenceMatcher(None, k, other).ratio()
            if ratio >= args.similarity:
                a_names = "/".join(sorted(labels[k]))
                b_names = "/".join(sorted(labels[other]))
                warnings.append(
                    f"possible name drift: '{a_names}' ({', '.join(sorted(where[k]))}) "
                    f"vs '{b_names}' ({', '.join(sorted(where[other]))})")

    # --- 4. Container table vs container diagram ----------------------------------
    # The real drift pattern: a component listed in the table but drawn nowhere, or drawn
    # but never described. Generic "appears only in a diagram" checks are too noisy on
    # deployment views, where labels carry ports, CIDRs and sizing detail.
    for a in artifacts:
        table_components = extract_table_components(a.raw)
        if not table_components:
            continue
        diagram_norm = [norm(l) for l in a.diagram_labels()]
        prose = re.sub(r"```mermaid.*?```", "", a.raw, flags=re.DOTALL)
        for comp in table_components:
            key = norm(comp)
            if not any(key in d or d in key for d in diagram_norm):
                warnings.append(
                    f"{a.name}: '{comp}' is listed in a component table but appears in no diagram")
        for lab in a.diagram_labels(name_filter="container"):
            key = norm(lab)
            if len(key) < 5:
                continue
            in_table = any(key in norm(c) or norm(c) in key for c in table_components)
            in_prose = norm(lab) in norm(prose)
            if not in_table and not in_prose:
                warnings.append(
                    f"{a.name}: '{lab}' is drawn but is in no component table and no prose")

    # --- 5. Same component, different technology in different places ---------------
    tech = defaultdict(set)
    for a in artifacts:
        for comp, t in TECH_HINT.findall(a.raw):
            comp = comp.strip()
            for token in KNOWN_TECH.findall(t):
                tech[norm(comp)].add((token.lower(), a.name, comp))
    for comp_key, entries in tech.items():
        tools = {t for t, _, _ in entries}
        if len(tools) > 1:
            display = sorted({c for _, _, c in entries})[0]
            detail = "; ".join(f"{t} in {f}" for t, f, _ in sorted(entries))
            errors.append(f"'{display}' is given different technologies: {detail}")

    # --- 6. Decisions table vs ADR files ------------------------------------------
    main_docs = [a for a in artifacts if a.get("type") == "architecture"]
    for doc in main_docs:
        listed = set()
        for line in doc.raw.splitlines():
            m = TABLE_ROW.match(line)
            if m:
                listed |= set(ADR_PATTERN.findall(m.group(1)))
        for adr in sorted(set(adr_files) - listed):
            if adr_status.get(adr) not in ("superseded", "deprecated", "retired"):
                warnings.append(f"{adr} exists but is not listed in {doc.name}'s decisions table")

    # --- 7. ADR structural completeness --------------------------------------------
    # An ADR missing Context or Consequences is the shape that fails a reader in two
    # years: it records what was chosen but not why, or the upside but not the cost.
    for adr_id, a in sorted(adr_files.items()):
        headings = {h.strip().lower() for h in HEADING.findall(a.body)}
        status = adr_status.get(adr_id, "")

        for required in ADR_REQUIRED_SECTIONS:
            if not any(required in h for h in headings):
                errors.append(f"{a.name}: no '{required.title()}' section — "
                              f"an ADR without it cannot be re-evaluated later")

        body_lower = a.body.lower()
        if "consequences" in " ".join(headings) and "negative" not in body_lower:
            warnings.append(f"{a.name}: consequences list no negatives — every real "
                            f"decision has costs, and an ADR without them reads as advocacy")

        if status not in ("superseded", "deprecated") and "retrospective" not in body_lower:
            options = len(OPTION_HEADING.findall(a.body))
            if options < 2:
                warnings.append(f"{a.name}: {options} option(s) considered — a decision with "
                                f"no recorded alternative gets relitigated")

        if a.get("state") == "transition" and not a.get("review_trigger"):
            errors.append(f"{a.name}: transitional decision with no review_trigger — "
                          f"this is how a temporary choice becomes permanent")

    # --- Report --------------------------------------------------------------------
    print(f"Checked {len(artifacts)} artifact(s), {len(adr_files)} ADR(s)")
    n_err = report(errors, "PROBLEMS")
    n_warn = report(warnings, "WARNINGS (review, may be legitimate)")
    if not (n_err or n_warn):
        print("\nNo contradictions found. Semantic contradictions still need a human — "
              "see references/review-and-validation.md")

    exit_code = 1 if (n_err or (args.strict and n_warn)) else 0

    if args.json:
        payload = {
            "checked": str(Path(args.path)),
            "artifacts": len(artifacts),
            "adrs": len(adr_files),
            "errors": errors,
            "warnings": warnings,
            "error_count": n_err,
            "warning_count": n_warn,
            "strict": args.strict,
            "exit_code": exit_code,
        }
        Path(args.json).parent.mkdir(parents=True, exist_ok=True)
        Path(args.json).write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        print(f"\nJSON report written to {args.json}")

    return exit_code


if __name__ == "__main__":
    sys.exit(main())
