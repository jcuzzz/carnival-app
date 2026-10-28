r"""Sanitise a raw CASES21 ST export before it is used in a local or
AI-assisted session.

NO OUTPUT OF THIS TOOL IS EVER COMMITTED (ADR-0033). Real-derived rows keep
real per-row attributes even after names are replaced, so both modes write
only to data/raw/ (git-ignored) or somewhere outside the repo entirely.
Committed test fixtures come from scripts/generate_synthetic_roster.py, which
invents every row.

Run this OUTSIDE any agentic workflow, on the raw file. Two modes:

  DEFAULT (de-identify) — for local inspection and AI-session use:
    - keeps ONLY the approved import columns (see ALLOWED below)
    - replaces STKEY with a one-way pseudonym (random per-run salt, never
      stored, so the code cannot be linked back to a student or matched
      across runs)
    - replaces all names with fake names drawn from pools
    - converts BIRTHDATE (Excel serial or d/m/Y) to 1 January of the birth
      year only (preserves age-group derivation, drops the actual birthday)
    - drops every other column (medical, family, contact, welfare, etc.)

  --minimise-only — keeps real STKEY/names (for authorised local testing of
    the import pipeline against real identity). Output still drops all
    non-approved columns and still truncates BIRTHDATE to 1 January.
    MUST NOT be pasted into AI sessions.

Usage:
  py scripts/sanitize_export.py <input.csv> <output.csv> [--minimise-only]
  py scripts/sanitize_export.py ST_0000.csv data/raw/ST_0000_deident.csv

The tool prints a report of what was kept, transformed and dropped so the
result can be eyeballed before use. Pure Python stdlib; no network access.
"""
import csv
import hashlib
import secrets
import sys
from datetime import date, timedelta
from pathlib import Path

# TITLE and SECOND_NAME are deliberately absent: the import mapping reads
# neither, and TITLE is gender-correlated.
ALLOWED = ["STKEY", "SURNAME", "FIRST_NAME", "PREF_NAME",
           "GENDER", "HOUSE", "STATUS", "BIRTHDATE",
           "HOME_GROUP", "SCHOOL_YEAR"]

FAKE_SURNAMES = ["Nguyen", "Papadopoulos", "OConnor", "Singh", "Kowalski",
                 "Ferrari", "Tanaka", "Okafor", "Andersson", "Castillo",
                 "Bakker", "Novak", "Ivanov", "Haddad", "Mackenzie", "Costa",
                 "Berg", "Silva", "Reyes", "Larsen", "Walker", "Hughes"]
FAKE_FIRSTS = ["Alex", "Sam", "Jordan", "Taylor", "Morgan", "Riley", "Casey",
               "Quinn", "Avery", "Rowan", "Harper", "Finley", "Sage",
               "Emerson", "Dakota", "Reese", "Skyler", "Blake", "Ellis",
               "Marley", "Noa", "Remy"]

EXCEL_EPOCH = date(1899, 12, 30)


def parse_birthdate(raw: str) -> date | None:
    raw = raw.strip()
    if not raw:
        return None
    if " " in raw:  # strip trailing time component, e.g. "26/11/1990 0:00"
        raw = raw.split(" ", 1)[0]
    if raw.isdigit():  # Excel day serial (CASES21 default)
        return EXCEL_EPOCH + timedelta(days=int(raw))
    for sep in ("/", "-"):
        parts = raw.split(sep)
        if len(parts) == 3:
            try:
                if len(parts[0]) == 4:  # ISO: Y-M-D / Y/M/D
                    y, m, d = (int(p) for p in parts)
                else:
                    d, m, y = (int(p) for p in parts)
                    if y < 100:
                        y += 2000 if y < 30 else 1900
                return date(y, m, d)
            except ValueError:
                pass
    return None


def digest(value: str, salt: str) -> int:
    return int.from_bytes(hashlib.sha256(f"{salt}|{value}".encode()).digest()[:8], "big")


def main() -> None:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    minimise_only = "--minimise-only" in sys.argv
    if len(args) != 2:
        print(__doc__)
        sys.exit(2)
    src, dst = Path(args[0]), Path(args[1])

    # Guard: output of either mode is derived from real rows and must never be
    # committable — refuse to write anywhere in the repo except data/raw/.
    repo_root = Path(__file__).resolve().parent.parent
    try:
        rel = dst.resolve().relative_to(repo_root)
        if rel.parts[:2] != ("data", "raw"):
            sys.exit(f"REFUSED: output is derived from a real export and is never committed; "
                     f"write it under data/raw/ (git-ignored) or outside the repo, not {rel}")
    except ValueError:
        pass  # outside the repo entirely is acceptable

    for encoding in ("utf-8-sig", "cp1252"):
        try:
            with src.open(newline="", encoding=encoding) as f:
                reader = csv.DictReader(f)
                source_cols = reader.fieldnames or []
                rows = [r for r in reader if any((v or "").strip() for v in r.values())]
            break
        except UnicodeDecodeError:
            continue
    else:
        sys.exit(f"ERROR: could not decode {src} as utf-8 or cp1252")

    missing = [c for c in ALLOWED if c not in source_cols]
    dropped = [c for c in source_cols if c not in ALLOWED]

    # Per-run and never stored: the pseudonym cannot be reversed by rehashing
    # a known STKEY, and does not survive across runs.
    run_salt = secrets.token_hex(16)

    out_rows = []
    seen_keys: dict[str, str] = {}
    for row in rows:
        rec = {c: (row.get(c) or "").strip() for c in ALLOWED}

        bd = parse_birthdate(rec["BIRTHDATE"])
        rec["BIRTHDATE"] = f"{bd.year}-01-01" if bd else ""

        if not minimise_only:
            key = rec["STKEY"]
            if key not in seen_keys:
                # STKEY is unique in CASES21; the pseudonym must be too.
                # 10^10 hash space + explicit collision bump guarantees it
                # (the old 10^5 space caused ~122 birthday collisions at 5k rows).
                salt = run_salt
                while True:
                    candidate = f"SYN{digest(key, salt) % 10**10:010d}"
                    if candidate not in seen_keys.values():
                        break
                    salt += "+"
                seen_keys[key] = candidate
            rec["STKEY"] = seen_keys[key]
            h = digest(key, f"{run_salt}|names")
            sur = FAKE_SURNAMES[h % len(FAKE_SURNAMES)]
            first = FAKE_FIRSTS[(h >> 16) % len(FAKE_FIRSTS)]
            pref = first if rec["PREF_NAME"] in ("", rec["FIRST_NAME"]) else FAKE_FIRSTS[(h >> 48) % len(FAKE_FIRSTS)]
            rec.update(SURNAME=sur, FIRST_NAME=first, PREF_NAME=pref)

        out_rows.append(rec)

    dst.parent.mkdir(parents=True, exist_ok=True)
    with dst.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=ALLOWED)
        w.writeheader()
        w.writerows(out_rows)

    mode = "MINIMISE-ONLY (real identity kept — keep out of git and AI sessions)" \
        if minimise_only else "DE-IDENTIFIED (one-way pseudonyms — never commit; local/AI use only)"
    print(f"mode:            {mode}")
    print(f"rows:            {len(out_rows)}")
    print(f"columns kept:    {len(ALLOWED)} ({', '.join(ALLOWED)})")
    print(f"columns dropped: {len(dropped)} (incl. medical/family/contact/welfare)")
    if missing:
        print(f"WARNING — expected columns missing from source: {', '.join(missing)}")
    if not minimise_only:
        n_dupes = len(out_rows) - len({r["STKEY"] for r in out_rows})
        print(f"pseudonym STKEY uniqueness: {'OK' if n_dupes == 0 else f'FAILED ({n_dupes} duplicates)'}")
    print("BIRTHDATE truncated to 1 January of birth year (age-group derivation preserved).")
    print(f"wrote {dst}")


if __name__ == "__main__":
    main()
