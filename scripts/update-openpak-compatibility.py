#!/usr/bin/env python3
"""Regenerate docs/openpak-compatibility.csv from the OpenPak website's catalogue.

The website database is the only source of title status. This keeps its Switch titles at live,
beta or alpha, which is what the emulator shows. A playable title without a title_id can't be
looked up by the emulator, so it is listed and the script fails without writing: set the id on
the website (PATCH /api/v1/admin/titles/{id}), not here.
"""

import csv
import io
import json
import re
import sys
import urllib.request
from pathlib import Path

URL = "https://openpak.org/api/v1/catalog.json"
CSV = Path(__file__).resolve().parent.parent / "docs" / "openpak-compatibility.csv"
PLAYABLE = {"live", "beta", "alpha"}


def main():
    request = urllib.request.Request(URL, headers={"User-Agent": "ryujinx-openpak-compatibility"})
    with urllib.request.urlopen(request, timeout=60) as response:
        catalog = json.load(response)

    titles = [e for e in catalog if e["kind"] == "title" and e["console"] == "Switch" and e["status"] in PLAYABLE]
    missing = [e["name"] for e in titles if not re.fullmatch(r"[0-9A-F]{16}", e.get("title_id") or "")]

    if missing:
        print(f"{len(missing)} playable Switch title(s) have no title_id on the website:", file=sys.stderr)
        for name in missing:
            print(f"  {name}", file=sys.stderr)
        sys.exit(1)

    rows = sorted(([e["title_id"], e["name"], e["backend"], e["status"]] for e in titles), key=lambda r: r[1].casefold())

    out = io.StringIO()
    writer = csv.writer(out, lineterminator="\n")
    writer.writerow(["title_id", "game_name", "backend", "status"])
    writer.writerows(rows)

    old = {r["title_id"]: r for r in csv.DictReader(CSV.open())} if CSV.exists() else {}
    new = {r[0]: r for r in rows}
    for tid in sorted(old.keys() - new.keys(), key=lambda t: old[t]["game_name"].casefold()):
        print(f"- {old[tid]['game_name']} ({old[tid]['status']})")
    for tid, (_, name, _, status) in new.items():
        if tid not in old:
            print(f"+ {name} ({status})")
        elif old[tid]["status"] != status:
            print(f"~ {name}: {old[tid]['status']} -> {status}")

    CSV.write_text(out.getvalue())


if __name__ == "__main__":
    main()
