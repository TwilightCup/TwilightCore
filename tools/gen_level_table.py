#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Generate a Markdown table mapping level IDs to their localized display names.

The display names come straight from the game's own localisation table, and the
row order mirrors the in-game level order, so the table stays correct when the
game adds new levels — just re-run the script.

Data sources (paths overridable via --game-dir / --config):
  * <game>/Human_Data/sharedassets0.assets
      Contains the "HFFMasterLocalisationFile" TextAsset as plain text: a CSV
      with header `Key,Type,Desc,English,French,...`.  The game parses exactly
      this CSV at runtime (I2.Loc LocalizationManager.Load), so extracting the
      English (or any other) column here matches what the game displays.
      The same file also stores the game's `levels[]` (scene names) and
      `editorPickLevels[]` (internal names) string arrays, which give the
      in-game level order used for sorting the table.
  * <game>/BepInEx/config/LevelCollections.json
      The collections config; used to decide which IDs "we use".

Usage:
  python3 tools/gen_level_table.py                 # levels used in the config
  python3 tools/gen_level_table.py --all           # every level in the loc table
  python3 tools/gen_level_table.py --lang "Chinese Simplified"
  python3 tools/gen_level_table.py -o docs/LEVEL_TABLE.md
"""

import argparse
import csv
import io
import json
import re
import struct
import sys
from pathlib import Path

DEFAULT_GAME_DIR = Path.home() / ".local/share/Steam/steamapps/common/Human Fall Flat"
DEFAULT_CONFIG = Path.home() / ".local/share/Steam/steamapps/common/Human Fall Flat" / "BepInEx/config/LevelCollections.json"

# Fallback in-game order, used only when the arrays cannot be extracted from the
# assets (e.g. the game structure changes).  Synced with:
#   CollectionManager.cs  — _builtInDisplayNameToIndex (levels[] order)
#   WorkshopRepository.cs — LoadBuiltinLevels / LoadEditorPickLevels
BUILTIN_IDS = [
    "Intro", "Train", "Carry", "Climb", "Break", "Siege", "Water", "Power",
    "Aztec", "Halloween", "Steam", "Ice", "Intro_Reprise", "Credits",
]
EDITOR_PICK_IDS = [
    "Thermal", "Factory", "Golf", "City", "Forest", "Lab", "Lumber", "RedRock",
    "Tower", "Miniature", "CopperWorld", "Naval_Ben", "OceanAdventure",
    "Dockyard", "Museum", "Hike", "Candyland", "Facility", "SteamPunk", "Viking",
]

# Scene names in `levels[]` that differ from the display name used as LevelId.
SCENE_TO_DISPLAY = {
    "Push": "Train",
    "River": "Water",
    "Steam_merged": "Steam",
    "Ice_merged": "Ice",
}

# Levels present in the game's data but NOT registered as playable levels
# (leftover localisation / array entries with no WorkshopRepository registration
# in the shipped game code, so they never appear in the in-game level list):
#   "Naval"    — old name of Naval_Ben; still sits in editorPickLevels[] at
#                index 11, but workshopId 11 is skipped (only Naval_Ben @12 is
#                registered) and its loc row has no real translations.
#   "Rooftops" — has a LEVEL/ term (same translations as City) and a scene
#                bundle ("rooftopsscene"), but is in neither levels[] nor
#                editorPickLevels[].
EXCLUDED_LEVEL_IDS = {"Naval", "Rooftops"}

# Levels with no entry in the localisation table.  The display names below are
# the ones already used in README/README_zh (Intro_Reprise is shown as "Reprise"
# in-game; it has no LEVEL/ term in the loc table).
LOC_FALLBACK = {
    "Intro_Reprise": "Reprise",
}

# Header labels used for the markdown table, keyed by localisation column name.
HEADER_OVERRIDES = {
    "English": "Display Name",
    "Chinese Simplified": "显示名称",
    "Chinese Taiwan": "顯示名稱",
}


class LocalisationTable:
    """Parse the game's localisation CSV (extracted from sharedassets0.assets)."""

    def __init__(self, text):
        self._lang_cols = {}
        self._levels = {}  # levelId -> {language: display name}

        rows = list(csv.reader(io.StringIO(text, newline="")))
        if not rows:
            raise ValueError("localisation CSV is empty")
        header = rows[0]
        self._lang_cols = {name: i for i, name in enumerate(header) if name}

        for row in rows[1:]:
            if not row or not row[0]:
                continue
            key = row[0]
            if not key.startswith("LEVEL/"):
                continue
            level_id = key[len("LEVEL/"):]
            if "/" in level_id:
                # e.g. LEVEL/SteamPunk/DESC — description term, not a level
                continue
            self._levels.setdefault(level_id, {})
            for lang, idx in self._lang_cols.items():
                self._levels[level_id][lang] = row[idx] if idx < len(row) else ""

    @property
    def languages(self):
        return list(self._lang_cols)

    def display_name(self, level_id, lang):
        """Localized name for a level, or None when the level has no term."""
        entry = self._levels.get(level_id)
        if entry is None:
            return None
        return entry.get(lang) or None

    def all_level_ids(self):
        return sorted(self._levels)


def parse_str_array(data, start):
    """Parse a Unity string array at `start`: [count:u32] then (len:u32 + bytes,
    4-aligned) per entry.  Returns (items, end_offset)."""
    count = struct.unpack_from("<I", data, start)[0]
    if count <= 0 or count > 1000:
        raise ValueError(f"implausible string array count {count}")
    pos = start + 4
    items = []
    for _ in range(count):
        ln = struct.unpack_from("<I", data, pos)[0]
        pos += 4
        if ln <= 0 or ln > 200:
            raise ValueError(f"implausible string length {ln}")
        items.append(data[pos:pos + ln].decode("utf-8", errors="replace"))
        pos += ln
        pos = (pos + 3) & ~3  # align to 4 bytes
    return items, pos


def find_level_arrays(game_dir):
    """Extract the in-game level order from sharedassets0.assets.

    Returns (builtin_display_order, editorpick_order) — display-name strings in
    game order.  Falls back to the static lists (BUILTIN_IDS / EDITOR_PICK_IDS)
    if the arrays cannot be located or validated.
    """
    path = Path(game_dir) / "Human_Data" / "sharedassets0.assets"
    if not path.exists():
        raise SystemExit(f"game assets not found: {path} (pass --game-dir?)")
    data = path.read_bytes()

    # levels[] starts with count + "Intro" and contains the unique scene names
    # "Push" and "River" — use those as validation.
    builtin_display, levels_end = None, None
    pos = 0
    while True:
        idx = data.find(b"\x05\x00\x00\x00Intro", pos)
        if idx < 0:
            break
        if idx >= 4:
            count = struct.unpack_from("<I", data, idx - 4)[0]
            if 5 <= count <= 60:
                try:
                    items, end = parse_str_array(data, idx - 4)
                except ValueError:
                    items = None
                if (items and items[0] == "Intro"
                        and "Push" in items and "River" in items):
                    builtin_display = [SCENE_TO_DISPLAY.get(s, s) for s in items]
                    levels_end = end
                    break
        pos = idx + 1

    if builtin_display is None:
        print("warning: could not locate levels[] in %s; using fallback order" % path,
              file=sys.stderr)
        return BUILTIN_IDS, EDITOR_PICK_IDS

    # editorPickLevels[] usually follows levels[] immediately; validate that it
    # starts with the known first EditorPick "Thermal".
    editorpick_order = None
    try:
        items, _ = parse_str_array(data, levels_end)
        if items and items[0] == "Thermal":
            editorpick_order = items
    except ValueError:
        pass
    if editorpick_order is None:
        print("warning: could not locate editorPickLevels[] in %s; "
              "using fallback order" % path, file=sys.stderr)
        return builtin_display, EDITOR_PICK_IDS

    return builtin_display, editorpick_order


def load_config_level_ids(config_path):
    """All distinct level IDs used anywhere in the collections config."""
    try:
        cfg = json.loads(Path(config_path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        raise SystemExit(f"failed to read config {config_path}: {e}")
    seen, ids = set(), []
    for col in cfg.get("Collections", []) or []:
        for lvl in col.get("Levels", []) or []:
            if lvl not in seen:
                seen.add(lvl)
                ids.append(lvl)
    return ids


def classify(level_id, builtin_order, editorpick_order):
    """Mirror CollectionManager.ResolveLevelType (id string -> type label)."""
    if level_id in builtin_order:
        return "BuiltIn"
    if level_id in editorpick_order:
        return "EditorPick"
    if level_id.startswith("lvl:") or "/" in level_id or "\\" in level_id:
        return "LocalWorkshop"
    if re.fullmatch(r"\d+", level_id):
        return "Workshop"
    return "?"  # in the loc table but not in any game array (new/unused level)


def sort_key(level_id, ltype, builtin_order, editorpick_order):
    """Sort by (type, in-game index); workshop/unknown levels go last."""
    if ltype == "BuiltIn":
        try:
            return (0, builtin_order.index(level_id))
        except ValueError:
            return (0, len(builtin_order))
    if ltype == "EditorPick":
        try:
            return (1, editorpick_order.index(level_id))
        except ValueError:
            return (1, len(editorpick_order))
    if ltype in ("Workshop", "LocalWorkshop"):
        return (2, 0)
    return (3, 0)


def markdown_table(loc_table, rows, lang):
    """rows: list of (level_id, type). Returns markdown source."""
    header_label = HEADER_OVERRIDES.get(lang, lang)
    out = ["| ID | {0} | Type |".format(header_label), "|---|---|---|"]
    for level_id, ltype in rows:
        name = loc_table.display_name(level_id, lang)
        if name is None:
            name = LOC_FALLBACK.get(level_id)
        if name is None:
            if ltype in ("Workshop", "LocalWorkshop"):
                name = "*(from Workshop metadata)*"
            else:
                name = "`" + level_id + "`"
        out.append("| `{0}` | {1} | {2} |".format(level_id, name, ltype))
    return "\n".join(out) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game-dir", type=Path, default=DEFAULT_GAME_DIR,
                    help="game install directory (default: %(default)s)")
    ap.add_argument("--config", type=Path, default=DEFAULT_CONFIG,
                    help="LevelCollections.json path (default: %(default)s)")
    ap.add_argument("--lang", default="English",
                    help="localisation column to use (default: %(default)s); "
                         "e.g. 'Chinese Simplified'")
    ap.add_argument("--all", action="store_true",
                    help="list every level in the localisation table instead of "
                         "only the ones used in the config")
    ap.add_argument("-o", "--output", type=Path,
                    help="write the table to this file instead of stdout")
    args = ap.parse_args()

    builtin_order, editorpick_order = find_level_arrays(args.game_dir)
    loc = LocalisationTable(find_localisation_csv(args.game_dir))

    if args.lang not in loc.languages:
        ap.error("unknown language %r; available: %s"
                 % (args.lang, ", ".join(loc.languages)))

    if args.all:
        # every level in the loc table, plus known IDs that have no loc term
        # (e.g. Intro_Reprise) so the full table matches the README; leftovers
        # without a WorkshopRepository registration are dropped.
        all_ids = set(loc.all_level_ids())
        all_ids.update(BUILTIN_IDS)
        all_ids.update(EDITOR_PICK_IDS)
        all_ids.difference_update(EXCLUDED_LEVEL_IDS)
        rows = [(lid, classify(lid, builtin_order, editorpick_order))
                for lid in sorted(all_ids)]
    else:
        if not args.config.exists():
            ap.error("config not found: %s (pass --config or use --all)"
                     % args.config)
        ids = load_config_level_ids(args.config)
        if not ids:
            ap.error("config %s defines no levels" % args.config)
        rows = [(lid, classify(lid, builtin_order, editorpick_order))
                for lid in ids]

    # In-game order: BuiltIn by levels[], EditorPick by editorPickLevels[];
    # python's sort is stable, so workshop rows keep their config order.
    rows.sort(key=lambda it: sort_key(it[0], it[1], builtin_order, editorpick_order))

    table = markdown_table(loc, rows, args.lang)

    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(table, encoding="utf-8")
        print("wrote %s (%d rows)" % (args.output, len(rows)))
    else:
        sys.stdout.write(table)


def find_localisation_csv(game_dir):
    """Extract the HFFMasterLocalisationFile text blob from sharedassets0.assets.

    The TextAsset payload is stored as a plain UTF-8 text run; we locate the
    CSV header line and read until the first byte that cannot be part of text.
    """
    assets = Path(game_dir) / "Human_Data" / "sharedassets0.assets"
    if not assets.exists():
        raise SystemExit(f"game assets not found: {assets} (pass --game-dir?)")
    data = assets.read_bytes()
    start = data.find(b"Key,Type,")
    if start < 0:
        raise SystemExit(f"localisation CSV header not found in {assets}")

    end = start
    while end < len(data):
        b = data[end]
        if b == 0 or (b < 32 and b not in (9, 10, 13)):
            break
        end += 1
    if end == start:
        raise SystemExit(f"empty localisation text in {assets}")

    return data[start:end].decode("utf-8", errors="replace")


if __name__ == "__main__":
    main()
