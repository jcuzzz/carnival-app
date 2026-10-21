"""Generate synthetic CASES21-shaped ST exports for import-pipeline testing.

Both shapes are wholly invented — no real export is ever an input (ADR-0033).

WIDE (default) — reproduces the full 227-column header (from
data/synthetic/cases21_st_header.txt) but only populates the columns the
platform imports (see config/import-mapping.example.json).
  Usage:  py scripts/generate_synthetic_roster.py [count] [outfile]
  Default: 120 students -> data/synthetic/ST_SYNTH.csv
  Deliberate edge cases: one duplicate STKEY, one missing HOUSE, one invalid
  SCHOOL_YEAR, one missing BIRTHDATE, a mix of ACTV / LEFT / LVNG statuses.

NARROW (--st-export) — the 12-column whole-school-history shape the platform
actually ingests, reproducing the real export's awkward properties so the
import pipeline is exercised honestly:
  Usage:  py scripts/generate_synthetic_roster.py --st-export [count] [outfile]
  Default: 5198 students -> data/synthetic/ST_CLEAN.csv
  - ~17% ACTV, the rest departed (LEFT with a few DEL / INAC / FUT)
  - departed birth years spanning 1983-2012; ACTV birth years school-age
  - legacy house codes (S12/S11/MS9/MS10/JR7/JR8) only on departed rows, plus
    current-era houses on both; HOUSE blank on many departed rows and on
    exactly one ACTV row (the unresolvable-exception case)
  - TITLE variety incl. blanks, blank SECOND_NAME, PREF_NAME differing from
    FIRST_NAME, HOME_GROUP correlated with SCHOOL_YEAR plus VC/ZZZ oddities
  - BIRTHDATE as YYYY-01-01 (birth year only — no full birth dates in fixtures)
"""
import csv, random, sys
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HEADER = (ROOT / "data/synthetic/cases21_st_header.txt").read_text(encoding="utf-8-sig").strip().split(",")

SURNAMES = ["Nguyen", "Papadopoulos", "OConnor", "Singh", "Kowalski", "Ferrari",
            "Tanaka", "Okafor", "Andersson", "Castillo", "Bakker", "Novak",
            "Ivanov", "Haddad", "Mackenzie", "Costa", "Berg", "Silva", "Reyes", "Larsen"]
FIRSTS = ["Alex", "Sam", "Jordan", "Taylor", "Morgan", "Riley", "Casey", "Quinn",
          "Avery", "Rowan", "Harper", "Finley", "Sage", "Emerson", "Dakota",
          "Reese", "Skyler", "Blake", "Ellis", "Marley"]
HOUSES = ["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"]  # match configured houses + real CASES21 data exactly
TITLES = ["Mr", "Ms", "Mx"]

ST_COLUMNS = ["STKEY", "SURNAME", "FIRST_NAME", "SECOND_NAME", "PREF_NAME", "TITLE",
              "GENDER", "HOUSE", "STATUS", "BIRTHDATE", "HOME_GROUP", "SCHOOL_YEAR"]
LEGACY_HOUSES = ["S12", "S11", "MS9", "MS10", "JR7", "JR8"]

EXCEL_EPOCH = date(1899, 12, 30)

def excel_serial(d: date) -> int:
    return (d - EXCEL_EPOCH).days

def main() -> None:
    count = int(sys.argv[1]) if len(sys.argv) > 1 else 120
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "data/synthetic/ST_SYNTH.csv"

    rng = random.Random(2027)
    rows, used_keys = [], set()

    for i in range(count):
        sur = rng.choice(SURNAMES)
        first = rng.choice(FIRSTS)
        key = f"{sur[:3].upper()}{i:04d}"
        while key in used_keys:
            key = f"{sur[:3].upper()}{rng.randint(0, 9999):04d}"
        used_keys.add(key)

        year = rng.randint(7, 12)
        birth_year = 2026 - (year + 5) - rng.choice([0, 0, 0, 1])
        bdate = date(birth_year, rng.randint(1, 12), rng.randint(1, 28))
        status = rng.choices(["ACTV", "LEFT", "LVNG"], weights=[85, 10, 5])[0]

        row = dict.fromkeys(HEADER, "")
        row.update({
            "STKEY": key,
            "SURNAME": sur,
            "FIRST_NAME": first,
            "SECOND_NAME": rng.choice(FIRSTS) if rng.random() < 0.5 else "",
            "PREF_NAME": first if rng.random() < 0.8 else rng.choice(FIRSTS),
            "TITLE": rng.choice(TITLES),
            "GENDER": rng.choices(["M", "F", "X"], weights=[48, 48, 4])[0],
            "HOUSE": rng.choice(HOUSES),
            "STATUS": status,
            "BIRTHDATE": str(excel_serial(bdate)),
            "HOME_GROUP": f"{year:02d}{rng.choice('ABCDE')}",
            "SCHOOL_YEAR": f"{year:02d}",
        })
        rows.append(row)

    # Edge cases for validation testing
    dup = dict(rows[0]); dup["FIRST_NAME"] = "Duplicate"; rows.append(dup)
    rows[1]["HOUSE"] = ""
    rows[2]["SCHOOL_YEAR"] = "XX"
    rows[3]["BIRTHDATE"] = ""

    with out.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=HEADER)
        w.writeheader()
        w.writerows(rows)
    print(f"wrote {len(rows)} rows -> {out}")


def st_title(rng: random.Random, gender: str) -> str:
    if gender == "M":
        return rng.choices(["Mr", "", "Mx"], weights=[97, 2, 1])[0]
    if gender == "F":
        return rng.choices(["Miss", "Ms", "Mrs", "", "Mx"], weights=[70, 23, 4, 2, 1])[0]
    return rng.choices(["Mx", "Ms", ""], weights=[70, 20, 10])[0]

def st_row(rng: random.Random, key: str, status: str) -> dict[str, str]:
    active = status == "ACTV"
    gender = rng.choices(["M", "F", "S"], weights=[515, 481, 4])[0]
    first = rng.choice(FIRSTS)
    if active:
        year = rng.randint(7, 12)
        birth_year = 2026 - (year + 5) - rng.choice([0, 0, 0, 1])
        house = rng.choice(HOUSES)
        home_group = f"{year:02d}{rng.choice('ABCDEF')}"
    else:
        # Departed rows carry their exit year (mostly 12) and decades of
        # legacy/blank house codes — the shape the validator must tolerate.
        year = rng.choices([12, 11, 10, 9, 8, 7], weights=[74, 8, 5, 5, 5, 3])[0]
        birth_year = rng.choices(range(1983, 2013), weights=range(1, 31))[0]
        legacy = rng.choices(LEGACY_HOUSES, weights=[47, 7, 4, 3, 2, 4])[0]
        house = rng.choices([legacy, "", rng.choice(HOUSES)], weights=[48, 39, 13])[0]
        home_group = rng.choices([f"{year:02d}{rng.choice('ABCDEF')}", "VC", "ZZZ", ""],
                                 weights=[92, 4, 3, 1])[0]
    return {
        "STKEY": key,
        "SURNAME": rng.choice(SURNAMES),
        "FIRST_NAME": first,
        "SECOND_NAME": rng.choice(FIRSTS) if rng.random() < 0.77 else "",
        "PREF_NAME": first if rng.random() < 0.84 else rng.choice(FIRSTS),
        "TITLE": st_title(rng, gender),
        "GENDER": gender,
        "HOUSE": house,
        "STATUS": status,
        "BIRTHDATE": f"{birth_year}-01-01",
        "HOME_GROUP": home_group,
        "SCHOOL_YEAR": f"{year:02d}",
    }

def st_export_main() -> None:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    count = int(args[0]) if args else 5198
    out = Path(args[1]) if len(args) > 1 else ROOT / "data/synthetic/ST_CLEAN.csv"

    rng = random.Random(20260822)
    active_count = round(count * 0.17)
    statuses = ["ACTV"] * active_count + ["DEL"] * 31 + ["INAC"] + ["FUT"]
    statuses += ["LEFT"] * (count - len(statuses))
    rng.shuffle(statuses)

    rows, used_keys = [], set()
    for status in statuses:
        key = f"SYN{rng.randrange(10**10):010d}"
        while key in used_keys:
            key = f"SYN{rng.randrange(10**10):010d}"
        used_keys.add(key)
        rows.append(st_row(rng, key, status))

    # Exactly one ACTV row with no house: the exception the pipeline must
    # surface for human resolution rather than promote or fail the batch.
    next(r for r in rows if r["STATUS"] == "ACTV")["HOUSE"] = ""

    with out.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=ST_COLUMNS)
        w.writeheader()
        w.writerows(rows)
    print(f"wrote {len(rows)} rows ({active_count} ACTV) -> {out}")

if __name__ == "__main__":
    st_export_main() if "--st-export" in sys.argv else main()
