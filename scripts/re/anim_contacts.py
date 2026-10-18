# usage: python -I scripts/re/anim_contacts.py [--json data/animation-contacts.json]
#
# The "contact" keyframe of every character class's animations: for each Characters.dat AOFile, follows the .ao's
# "extends" chain to its AnimationController metadata (.amd, e.g. Art/Models/CHARACTERS/Witch/rig.amd) and reads, per
# animation, its length and the time of its "contact" event, both in ms. That keyframe is when an attack lands and when
# a spell is released (Witch spell_projectile_wand: 1066 ms long, contact at 599). The Gateway reads the output to
# release projectiles at the right moment of the cast (Agents/docs/protocol/11-skills.md §9).
#
# .amd text (UTF-16): a version line, a count line, then per animation:
#   "<name>" / once|loop / <length ms> / <event count> / per event: "<event>" / <time ms> / <x y z>, then two lines.
# Files come from the GGPK through scripts/re/ggdump (see ot_components.py) and are cached in Tools/re/out/ot.
import json, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ot_components import REPO, cache_name, fetch, read_text  # noqa: E402


def amd_of(ao):
    """The AnimationController metadata of an .ao, following its extends chain."""
    seen = set()
    while ao and ao not in seen:
        seen.add(ao)
        fetch([ao])
        text = read_text(ao)
        if text is None:
            return None
        m = re.search(r'AnimationController\s*\{[^}]*?metadata\s*=\s*"([^"]+)"', text, re.S)
        if m:
            return m.group(1)
        m = re.search(r'extends\s+"([^"]+)"', text)
        ao = m.group(1) + ".ao" if m and not m.group(1).endswith(".ao") else (m.group(1) if m else None)
    return None


def contacts(amd):
    fetch([amd])
    if not os.path.exists(cache_name(amd)):
        return {}
    raw = open(cache_name(amd), "rb").read()
    # .amd files are UTF-16 without a byte order mark
    text = raw.decode("utf-16-le") if len(raw) > 1 and raw[1] == 0 else read_text(amd)
    lines = [l.strip() for l in text.replace("\r", "").split("\n")]
    out, i = {}, 0
    while i < len(lines):
        m = re.fullmatch(r'"([^"]+)"', lines[i])
        if m and i + 3 < len(lines) and lines[i + 1] in ("once", "loop") and lines[i + 2].isdigit() and lines[i + 3].isdigit():
            name, length, events, j = m.group(1), int(lines[i + 2]), int(lines[i + 3]), i + 4
            contact = None
            for _ in range(events):
                ev = lines[j].strip('"')
                if ev == "contact" and contact is None and lines[j + 1].lstrip("-").isdigit():
                    contact = int(lines[j + 1])
                j += 3
            if contact is not None:
                out[name] = [length, contact]
            i = j
        else:
            i += 1
    return out


def main():
    dest = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else os.path.join(REPO, "data", "animation-contacts.json")
    chars = json.load(open(os.path.join(REPO, "data", "tables", "Characters.json"), encoding="utf-8"))
    result = {}
    for c in chars:
        ao = c.get("AOFile")
        if not ao or ao in result:
            continue
        amd = amd_of(ao)
        result[ao] = {"amd": amd, "animations": contacts(amd) if amd else {}}
        print(f"{c['Name']}: {ao} -> {amd}, {len(result[ao]['animations'])} animations with a contact", file=sys.stderr)
    with open(dest, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    print(f"wrote {dest}", file=sys.stderr)


if __name__ == "__main__":
    main()
