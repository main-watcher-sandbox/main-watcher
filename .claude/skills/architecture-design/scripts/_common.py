"""Shared parsing helpers for the architecture check scripts.

Deliberately dependency-free: these run in CI and in throwaway environments where
installing PyYAML is friction nobody needs for a dozen scalar fields.
"""

import re
from pathlib import Path

ID_PATTERN = re.compile(r"\b((?:US|FR|NFR|EP|ADR|ARCH|TM|TS|MP|IDX|TD|R|Q|A|C|F|AB)-\d+)\b")
ADR_PATTERN = re.compile(r"\bADR-\d+\b")
UNCONFIRMED = re.compile(r"\[unconfirmed\]", re.IGNORECASE)
ASSUMPTION = re.compile(r"\[assumption\]", re.IGNORECASE)
OPEN_ITEM = re.compile(r"\[open\]", re.IGNORECASE)
MERMAID_BLOCK = re.compile(r"```mermaid\s*\n(.*?)```", re.DOTALL)

# Quoted labels: api["Claims API<br/>[FastAPI]"] · db[("Claims DB")] · q[["Events"]]
QUOTED_LABEL = re.compile(r'[\w-]+\s*(?:\[\[|\[\(|\(\[|\[|\{)\s*"([^"]+)"')
# Unquoted labels: queue[[Order Events]] · U([Shopper]) · svc[Order API]
# Bare "(" is deliberately excluded: it would capture parenthetical text inside
# sequence-diagram messages, e.g. "POST /claims (Idempotency-Key: ...)".
UNQUOTED_LABEL = re.compile(r'[\w-]+\s*(?:\[\[|\[\(|\(\[|\[|\{)\s*([^"\[\]{}()|<>-][^\[\]{}()|]*)')
PARTICIPANT = re.compile(r"^\s*(?:participant|actor)\s+\S+(?:\s+as\s+(.+))?$", re.MULTILINE)
BLOCK_NAME = re.compile(r"^%%\s*name\s*:\s*(.+?)\s*$", re.MULTILINE)

VALID_STATUS = {"draft", "proposed", "agreed", "accepted", "superseded", "deprecated", "retired"}
VALID_STATE = {"current", "target", "transition"}
VALID_CONFIDENCE = {"confirmed", "assumed", "unverified"}
REQUIRED_FIELDS = ("id", "type", "status", "owner", "reviewed")


def parse_frontmatter(text):
    """Parse simple YAML frontmatter into a dict. Returns ({}, text) when absent."""
    if not text.startswith("---"):
        return {}, text
    end = text.find("\n---", 3)
    if end == -1:
        return {}, text
    block, body = text[3:end], text[end + 4:]
    data = {}
    for line in block.splitlines():
        line = line.split("#", 1)[0].rstrip() if not line.strip().startswith("#") else ""
        if not line.strip() or ":" not in line:
            continue
        key, _, value = line.partition(":")
        key, value = key.strip(), value.strip()
        if value.startswith("[") and value.endswith("]"):
            inner = value[1:-1].strip()
            data[key] = [v.strip().strip("'\"") for v in inner.split(",") if v.strip()]
        else:
            data[key] = value.strip("'\"")
    return data, body


class Artifact:
    def __init__(self, path):
        self.path = Path(path)
        raw = self.path.read_text(encoding="utf-8", errors="replace")
        self.meta, self.body = parse_frontmatter(raw)
        self.raw = raw

    @property
    def name(self):
        return self.path.name

    @property
    def id(self):
        return self.meta.get("id") or self.path.stem.upper()

    def get(self, key, default=""):
        return self.meta.get(key, default)

    def mermaid_blocks(self):
        return MERMAID_BLOCK.findall(self.body)

    def diagram_labels(self, name_filter=None):
        """Element labels from diagrams that name components.

        `name_filter` restricts to blocks whose `%% name:` contains that substring —
        used to check the container view specifically, since deployment and network
        views legitimately contain infrastructure nodes that are not containers.

        ER and state diagrams are skipped: their syntax (`A ||--o{ B : verb`,
        `S1 --> S2 : event`) looks like labelled nodes but names entities and states,
        which would pollute a component-naming comparison with false drift.
        """
        labels = set()
        for block in self.mermaid_blocks():
            head = next((l.strip() for l in block.splitlines()
                         if l.strip() and not l.strip().startswith("%%")), "")
            if not head.startswith(("flowchart", "graph", "sequenceDiagram", "classDiagram")):
                continue
            if name_filter is not None:
                m = BLOCK_NAME.search(block)
                if not m or name_filter not in m.group(1).lower():
                    continue
            for line in block.splitlines():
                stripped = line.strip()
                if (not stripped or stripped.startswith("%%")
                        or stripped.startswith(("class ", "classDef", "style ", "click ",
                                                "subgraph", "note ", "Note "))):
                    continue
                # In sequence diagrams only participant/actor lines name elements;
                # message text is prose and would produce phantom components.
                if head.startswith("sequenceDiagram") and not stripped.startswith(
                        ("participant", "actor")):
                    continue
                found = QUOTED_LABEL.findall(stripped)
                if not found and '"' not in stripped:
                    found = UNQUOTED_LABEL.findall(stripped)
                for raw in found:
                    lab = clean_label(raw)
                    if lab:
                        labels.add(lab)
            for m in PARTICIPANT.finditer(block):
                if m.group(1):
                    lab = clean_label(m.group(1))
                    if lab:
                        labels.add(lab)
        return labels


def clean_label(label):
    """Normalise a diagram label to its display name, dropping bracketed technology hints."""
    label = re.sub(r"<br\s*/?>", " ", label)
    label = re.sub(r"\[.*?\]", " ", label)          # [FastAPI], [person]
    label = re.sub(r"\(.*?\)", " ", label)          # (v2)
    label = re.sub(r'["\'`]', "", label)
    label = re.sub(r"\s+", " ", label).strip(" :-–—")
    if len(label) < 3 or len(label) > 60:
        return ""
    if label.lower() in {"yes", "no", "ok", "end", "true", "false"}:
        return ""
    return label


def collect_artifacts(root):
    root = Path(root)
    paths = [root] if root.is_file() else sorted(
        p for p in root.rglob("*.md") if "node_modules" not in p.parts
    )
    return [Artifact(p) for p in paths]


def norm(name):
    """Aggressive normalisation for comparing element names across artifacts."""
    return re.sub(r"[^a-z0-9]", "", name.lower())
