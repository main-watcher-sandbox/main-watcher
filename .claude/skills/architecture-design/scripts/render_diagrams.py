#!/usr/bin/env python3
"""Validate and render Mermaid diagrams embedded in markdown.

Keeps the Mermaid source as the single source of truth inside the markdown
(so it stays diffable and renders on GitHub) and exports image files beside it
for wikis, slide decks and Word documents that cannot render Mermaid.

Usage:
    python3 render_diagrams.py docs/architecture/          # render every .md found
    python3 render_diagrams.py docs/architecture.md --check  # validate syntax only
    python3 render_diagrams.py docs/ --out docs/diagrams --format svg

Naming: a diagram is named by a `%% name: my-diagram` comment on the first line
of the block, otherwise by the nearest preceding markdown heading, otherwise by
position. Names are slugified and de-duplicated.
"""

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

CACHE = Path(os.environ.get("ARCH_DIAGRAM_CACHE", Path.home() / ".cache" / "arch-diagrams"))
FENCE = re.compile(r"^(?P<indent>[ \t]*)```+\s*mermaid\s*$", re.IGNORECASE)
HEADING = re.compile(r"^#{1,6}\s+(.*?)\s*#*$")
NAME_HINT = re.compile(r"^%%\s*name\s*:\s*(.+?)\s*$", re.IGNORECASE | re.MULTILINE)


def slug(text, fallback="diagram"):
    s = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    return s[:60] or fallback


def find_mermaid_blocks(md_path):
    """Return [{name, code, line}] for each ```mermaid block in a markdown file."""
    lines = md_path.read_text(encoding="utf-8").splitlines()
    blocks, heading, i = [], "", 0
    while i < len(lines):
        h = HEADING.match(lines[i])
        if h:
            heading = h.group(1)
        m = FENCE.match(lines[i])
        if not m:
            i += 1
            continue
        start, body = i + 1, []
        i += 1
        while i < len(lines) and not re.match(r"^[ \t]*```+\s*$", lines[i]):
            body.append(lines[i])
            i += 1
        i += 1
        code = "\n".join(body).strip()
        if not code:
            continue
        hint = NAME_HINT.search(code)
        name = slug(hint.group(1)) if hint else slug(heading, f"diagram-{len(blocks) + 1}")
        blocks.append({"name": name, "code": code, "line": start})
    return blocks


def ensure_mermaid():
    """Return the path to a mermaid browser bundle, installing it if needed."""
    bundle = CACHE / "node_modules" / "mermaid" / "dist" / "mermaid.min.js"
    if bundle.exists():
        return bundle
    CACHE.mkdir(parents=True, exist_ok=True)
    (CACHE / "package.json").write_text('{"name":"arch-diagrams","private":true}\n')
    print("Installing mermaid (one-time, cached)...", file=sys.stderr)
    r = subprocess.run(
        # On Windows npm is a .cmd shim, which subprocess cannot run by the bare name.
        ["npm.cmd" if os.name == "nt" else "npm", "install", "mermaid", "--no-audit", "--no-fund", "--silent"],
        cwd=CACHE, capture_output=True, text=True,
    )
    if not bundle.exists():
        raise RuntimeError(
            "Could not install mermaid from npm. The Mermaid source in the markdown is "
            "still valid and will render on GitHub/Mermaid Live; only image export is "
            f"unavailable.\nnpm said: {r.stderr.strip()[:400]}"
        )
    return bundle


def build_page(bundle, theme):
    return (
        "<!doctype html><html><head><meta charset='utf-8'>"
        "<style>body{margin:0;background:#fff;"
        "font-family:system-ui,'Segoe UI',Helvetica,Arial,sans-serif}</style>"
        f"<script>{bundle.read_text(encoding='utf-8')}</script></head>"
        "<body><div id='out'></div></body></html>"
    )


RENDER_JS = """
async ({code, theme}) => {
  const out = document.getElementById('out');
  out.innerHTML = '';
  mermaid.initialize({startOnLoad:false, theme, securityLevel:'strict',
                      flowchart:{useMaxWidth:false}, sequence:{useMaxWidth:false},
                      er:{useMaxWidth:false}, class:{useMaxWidth:false}});
  try {
    await mermaid.parse(code);
    const {svg} = await mermaid.render('d' + Math.random().toString(36).slice(2), code);
    out.innerHTML = svg;
    return {ok: true, svg};
  } catch (e) {
    return {ok: false, error: (e && e.message ? e.message : String(e))};
  }
}
"""


def main():
    ap = argparse.ArgumentParser(description="Validate and render Mermaid blocks in markdown.")
    ap.add_argument("paths", nargs="+", help="Markdown files or directories to scan")
    ap.add_argument("--out", help="Output directory (default: <doc dir>/diagrams)")
    ap.add_argument("--format", choices=["svg", "png", "both"], default="both")
    ap.add_argument("--theme", default="neutral",
                    help="Mermaid theme: neutral, default, dark, forest, base")
    ap.add_argument("--scale", type=float, default=2.0, help="PNG pixel density")
    ap.add_argument("--check", action="store_true", help="Validate syntax only, write nothing")
    args = ap.parse_args()

    md_files = []
    for p in args.paths:
        path = Path(p)
        if path.is_dir():
            md_files += sorted(q for q in path.rglob("*.md") if "node_modules" not in q.parts)
        elif path.suffix.lower() in (".md", ".mmd", ".mermaid"):
            md_files.append(path)
    if not md_files:
        print("No markdown files found.", file=sys.stderr)
        return 1

    jobs, seen = [], {}
    for md in md_files:
        for b in find_mermaid_blocks(md):
            name = b["name"]
            seen[name] = seen.get(name, 0) + 1
            if seen[name] > 1:
                name = f"{name}-{seen[name]}"
            out_dir = Path(args.out) if args.out else md.parent / "diagrams"
            jobs.append({**b, "name": name, "src": md, "out_dir": out_dir})

    if not jobs:
        print("No mermaid blocks found. Diagrams belong in ```mermaid fenced blocks.")
        return 0

    try:
        bundle = ensure_mermaid()
        from playwright.sync_api import sync_playwright
    except Exception as e:
        print(f"Cannot render images: {e}", file=sys.stderr)
        return 2

    failures, written = [], []
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        page = browser.new_page(viewport={"width": 1800, "height": 1400},
                                device_scale_factor=args.scale)
        page.set_content(build_page(bundle, args.theme), wait_until="load")
        for job in jobs:
            res = page.evaluate(RENDER_JS, {"code": job["code"], "theme": args.theme})
            where = f"{job['src']}:{job['line']} ({job['name']})"
            if not res.get("ok"):
                failures.append({"where": where, "error": res.get("error", "unknown")})
                continue
            if args.check:
                continue
            job["out_dir"].mkdir(parents=True, exist_ok=True)
            if args.format in ("svg", "both"):
                f = job["out_dir"] / f"{job['name']}.svg"
                f.write_text(res["svg"], encoding="utf-8")
                written.append(f)
            if args.format in ("png", "both"):
                el = page.query_selector("#out svg")
                f = job["out_dir"] / f"{job['name']}.png"
                el.screenshot(path=str(f))
                written.append(f)
        browser.close()

    ok = len(jobs) - len(failures)
    print(f"{ok}/{len(jobs)} diagram(s) valid" + ("" if args.check else f", {len(written)} file(s) written"))
    for f in written:
        print(f"  {f}")
    for f in failures:
        print(f"  FAILED {f['where']}: {f['error'].splitlines()[0][:200]}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
