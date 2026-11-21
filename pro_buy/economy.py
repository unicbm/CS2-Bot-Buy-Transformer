"""Equipment, price quotes and transaction validation for CS2 team purchases."""
import itertools
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
PRIMARY = {7:"ak47", 8:"aug", 9:"awp", 10:"famas", 11:"g3sg1", 13:"galilar",
           14:"m249", 16:"m4a1", 17:"mac10", 19:"p90", 23:"mp5sd", 24:"ump45",
           25:"xm1014", 26:"bizon", 27:"mag7", 28:"negev", 29:"sawedoff", 33:"mp7",
           34:"mp9", 35:"nova", 38:"scar20", 39:"sg556", 40:"ssg08", 60:"m4a1_silencer"}
NAMES = dict(zip(("AK-47","AUG","AWP","FAMAS","G3SG1","Galil AR","M249","M4A4",
                  "MAC-10","P90","MP5-SD","UMP-45","XM1014","PP-Bizon","MAG-7","Negev",
                  "Sawed-Off","MP7","MP9","Nova","SCAR-20","SG 553","SSG 08","M4A1-S"),
                 PRIMARY.values(), strict=True))
ITEM = {name: next(i for i, code in PRIMARY.items() if code == symbol) for name, symbol in NAMES.items()}
ITEM.update({"Desert Eagle":1, "Dual Berettas":2, "Five-SeveN":3, "Glock-18":4, "Tec-9":30,
             "P2000":32, "P250":36, "USP-S":61, "CZ75-Auto":63, "R8 Revolver":64, "Zeus x27":31,
             "Kevlar Vest":50, "Kevlar & Helmet":51, "Defuse Kit":55, "Flashbang":43,
             "High Explosive Grenade":44, "Smoke Grenade":45, "Molotov":46, "Decoy Grenade":47,
             "Incendiary Grenade":48})
PISTOLS = (1,2,3,4,30,32,36,61,63,64)
GRENADES = (43,44,45,46,47,48)
SINGLES = [tuple(sorted(PRIMARY)), PISTOLS, (50,51), (55,), (31,)]
# Each slot has an explicit "buy nothing" action at index zero.
CHOICES = [[()] + [(i,) for i in group] for group in SINGLES]
CHOICES.append([g for n in range(5) for g in itertools.combinations_with_replacement(GRENADES,n)
                if all(g.count(i) <= (2 if i == 43 else 1) for i in g)])
LOOKUP = [{g:i for i,g in enumerate(slot)} for slot in CHOICES]
PORTABLE = set(PRIMARY) | set(PISTOLS) | set(GRENADES) | {31}

def equipment(snapshot):
    ids, armor, helmet, kit = snapshot
    def held(group):
        items = [i for i in ids if i in group]
        return items[0] if len(items) == 1 else (0 if not items else -1)
    return (held(PRIMARY), held(PISTOLS), int(armor), int(helmet), int(kit), int(31 in ids),
            *(ids.count(i) for i in GRENADES))


def after_buy(before, labels):
    p,s,armor,kit,zeus,grenades = [CHOICES[j][v] for j,v in enumerate(labels)]
    return (p[0] if p else before[0], s[0] if s else before[1], 100 if armor else before[2],
            int(armor == (51,) or before[3]), int(bool(kit) or before[4]), int(bool(zeus) or before[5]),
            *(x + grenades.count(i) for x,i in zip(before[6:],GRENADES,strict=True)))


def features(r):
    primary,pistol,armor,helmet,kit,zeus,*grenades = r["start"]
    return [r["cash"]/5000, r["team_cash"]/5000, r["team_primaries"]/5, r["side"]==3, armor/100,
            helmet,kit,zeus, r["round"] in (1,13), r["round"] in (12,24), r["own_score"]/12,
            r["opp_score"]/12, r["own_point"], r["opp_point"], r["overtime"], r["ot_half_round"]/3,
            (r["ot_reset_cash"] or 0)/10000, *grenades,
            *(primary==i for i in sorted(PRIMARY)), *(pistol==i for i in PISTOLS)]


def prices_for(row, menu):
    # An actual server caller may supply its native, already-quoted loadout menu instead.
    if "offers" in row:
        return {int(i):int(v) for i,v in row["offers"].items() if int(i) in ITEM.values() and v >= 0}
    offers = menu[row["side"]].copy()
    _,_,armor,helmet,*_ = row["start"]
    if 51 in offers:
        offers[51] = 650 if helmet else 350 if armor == 100 else 1000
    return offers


def apply_plan(team, edges, menus):
    """Stage all five ledgers, commit only a complete legal plan. No world pickup."""
    balances = [r["cash"] for r in team]
    received = [[] for _ in team]
    receipts = []
    for a,b,i in edges:
        price = menus[a].get(i)
        if (price is None or price < 0 or price > balances[a] or (a!=b and (i not in PORTABLE or team[a].get("human",False)))
                or (a!=b and "offers" in team[b] and menus[b].get(i)!=price)):
            return None
        balances[a] -= price
        received[b].append(i)
        receipts.append((a,b,i,price))
    result = []
    for r,items in zip(team,received,strict=True):
        base = r["start"]
        slots = [tuple(sorted(i for i in items if i in group)) for group in [*SINGLES,GRENADES]]
        if any(s not in d for s,d in zip(slots,LOOKUP,strict=True)):
            return None
        if (slots[0] and base[0] or slots[1] and base[1] not in (0,4,32,61)
            or slots[1] and slots[1]==(base[1],) or slots[3] and base[4] or slots[4] and base[5]
            or slots[2] and base[2]==100 and (slots[2]==(50,) or base[3])):
            return None
        state = after_buy(base,[d[s] for s,d in zip(slots,LOOKUP,strict=True)])
        if sum(state[6:])>4 or any(n>k for n,k in zip(state[6:],(2,1,1,1,1,1))):
            return None
        result.append(state)
    return dict(cash=balances,inventory=result,transactions=receipts)


def context(team, roles):
    return np.array([features(r)+[roles.get(r["name"].strip().lower())==i for i in range(4)] for r in team],dtype=np.float32)
