"""Predict player loadouts and fund them from the five actual wallets."""
import itertools

import numpy as np
import torch
from torch import nn
from torch.nn import functional as F
from economy import ROOT, PRIMARY, PISTOLS, GRENADES, PORTABLE, context, prices_for, apply_plan
from pistol import complete_rifle_buys

PATH = ROOT / "models/pro_buy_departure.pt"
GUNS = tuple(sorted(PRIMARY))
MASKS = np.array(list(itertools.product((0, 1), repeat=5)), dtype=int)
MASK_COUNTS = MASKS.sum(axis=1)


class DepartureNet(nn.Module):
    def __init__(self, width, kits):
        super().__init__()
        self.project = nn.Linear(width, 48)
        self.encoder = nn.TransformerEncoderLayer(48, 4, 96, dropout=.06, batch_first=True)
        self.count = nn.Linear(48, 6)
        self.assignment = nn.Linear(48, 6)
        self.personal = nn.Sequential(nn.Linear(width,32),nn.ReLU(),nn.Linear(32,1))
        self.recipient = nn.Sequential(nn.Linear(50, 64), nn.ReLU())
        self.weapon = nn.Linear(64, len(GUNS))
        self.kit = nn.Linear(64, kits)
        self.payer = nn.Sequential(nn.Linear(129, 32), nn.ReLU(), nn.Linear(32, 3))
        self.wallet = nn.Sequential(nn.Linear(width,48),nn.ReLU(),nn.Linear(48,2))
        self.register_buffer("identity", torch.eye(5)[None, :, :, None], persistent=False)

    def encode(self, x):
        h = self.encoder(self.project(x))
        # A player's own economy votes on both the number of guns and their owners.
        slots = self.assignment(h) + self.personal(x)
        return h, self.count(h.mean(1)), slots

    def cash_at_departure(self,x):
        # Dollars in thousands. Estimate both typical reserves and their spread.
        z=self.wallet(x)
        return torch.minimum(F.softplus(z[...,0]),x[...,0]*5), .1+F.softplus(z[...,1])

    def shopping(self, h, armed):
        count = armed.mean(1, keepdim=True).expand_as(armed)
        z = self.recipient(torch.cat([h, armed[:,:,None], count[:,:,None]], dim=2))
        receiver = z[:,:,None,:].expand(-1,-1,5,-1)
        payer = z[:,None,:,:].expand(-1,5,-1,-1)
        pairs = torch.cat([receiver, payer, self.identity.expand(len(z),-1,-1,-1)], dim=3)
        return self.weapon(z), self.kit(z), self.payer(pairs).transpose(2,3)


def fund_primaries(needed, cash, shops, human, gun_scores, pay_scores):
    """Keep several affordable allocations until every requested gun has a payer."""
    choices = {}
    for b in needed:
        options = []
        for j,item in enumerate(GUNS):
            price = shops[b].get(item)
            if price is None:
                continue
            for a,shop in enumerate(shops):
                if (a!=b and human[a]) or shop.get(item)!=price or cash[a]<price:
                    continue
                options.append((a,item,price,gun_scores[b,j]+pay_scores[b,0,a]))
        if not options:
            return None
        choices[b] = options
    beam = [(0., tuple(cash), ())]
    for b in sorted(needed, key=lambda b:len(choices[b])):
        best = {}
        for score,balances,edges in beam:
            for a,item,price,value in choices[b]:
                if balances[a] < price:
                    continue
                new = list(balances)
                new[a] -= price
                key = tuple(new)
                total = score+value
                if key not in best or total>best[key][0]:
                    best[key] = (total, key, edges+((a,b,item),))
        if not best:
            return None
        beam = sorted(best.values(),key=lambda v:-v[0])[:24]
    return list(beam[0][2])


def can_add_kit(before, kit):
    primary,pistol,armor,helmet,defuser,zeus,*grenades = before
    for item in kit:
        if item in PISTOLS and (pistol not in (0,4,32,61) or item==pistol):
            return False
        if item==55 and defuser or item==31 and zeus:
            return False
        if armor==100 and (item==50 or item==51 and helmet):
            return False
        if item in GRENADES:
            j = GRENADES.index(item)
            grenades[j] += 1
            if grenades[j]>(2 if item==43 else 1):
                return False
    return sum(grenades)<=4


def fund_kit(b, kit, balances, reserve, shops, human, pay):
    remaining = balances.copy()
    edges = []
    # Pay for personal equipment first; protect teammates' planned armor when
    # considering a sponsor. Only the engine's mutually available items transfer.
    for item in sorted(kit,key=lambda i:i in PORTABLE):
        price = shops[b].get(item)
        if price is None:
            return None
        group = 1 if item in PISTOLS else 2
        payers = [a for a in range(5) if shops[a].get(item)==price and
                  (a==b or item in PORTABLE and not human[a]) and
                  remaining[a]-(reserve[a] if a!=b else 0)>=price]
        if not payers:
            return None
        a = max(payers,key=lambda a:pay[b,group,a])
        remaining[a] -= price
        edges.append((a,b,item))
    return remaining,edges


class DeparturePlanner:
    def __init__(self, state, pistol_planner):
        self.roles,self.kits,self.menu = state["roles"],state["kits"],state["menu"]
        self.pistol_planner=pistol_planner
        self.model = DepartureNet(state["width"],len(self.kits)).eval()
        self.model.load_state_dict(state["weights"])
        self.armored = np.array([50 in k or 51 in k for k in self.kits])

    @classmethod
    def load(cls,pistol_planner):
        return cls(torch.load(PATH,weights_only=True),pistol_planner)

    @torch.inference_mode()
    def choose(self, team, rng=None, *, complete_rifles=False):
        # Standard pistol starts share the same five wallets. Intact observed
        # exchanges preserve who funds the armored recipient's upgraded pistol.
        if team[0]["round"] in (1,13) and all(
            r["cash"]==800 and not r["start"][0] and not r["start"][2] for r in team
        ):
            edges,covered=self.pistol_planner.choose(team,rng,complete_rifles=False)
            if covered:
                return edges,True
        shops = [prices_for(r,self.menu) for r in team]
        cash = np.array([r["cash"] for r in team])
        starts = [r["start"] for r in team]
        carried = np.array([bool(s[0]) for s in starts])
        human = [r.get("human",False) for r in team]
        x=torch.from_numpy(context(team,self.roles))[None]
        h, count, slots = self.model.encode(x)
        count = count[0].log_softmax(0).numpy()
        slots = slots[0].numpy()
        score = count[MASK_COUNTS]+(MASKS*slots[:,MASK_COUNTS].T).sum(1)
        median,spread=self.model.cash_at_departure(x)
        median,spread=median[0].numpy(),spread[0].numpy()
        head_cache={}
        proposals=[]
        likelihood=[]
        for _ in range(4 if rng is not None else 1):
            edges,remaining=self._propose(h,head_cache,score,carried,starts,cash,shops,human,rng)
            proposals.append(edges)
            likelihood.append(-np.sum(np.abs(remaining/1000-median)/spread))
        if rng is not None:
            probabilities=np.exp(likelihood-np.max(likelihood))
            edges=proposals[rng.choice(len(proposals),p=probabilities/probabilities.sum())]
        if complete_rifles:
            edges = complete_rifle_buys(team,edges,apply_plan(team,edges,shops),self.menu)
        return edges,True

    def _propose(self,h,head_cache,score,carried,starts,cash,shops,human,rng):
        if rng is not None:
            score = score + rng.gumbel(size=score.shape)
        valid = np.flatnonzero((MASKS>=carried).all(axis=1))
        for idx in valid[np.argsort(-score[valid])]:
            mask = MASKS[idx]
            if idx not in head_cache:
                outputs=self.model.shopping(h,torch.tensor(mask[None],dtype=torch.float32))
                head_cache[idx]=tuple(out[0].log_softmax(-1).numpy() for out in outputs)
            guns,kits,pay=head_cache[idx]
            reserve = np.array([min((shop[i] for i in (50,51) if i in shop),default=0)
                                if not s[2] and np.exp(k[self.armored]).sum()>.5 else 0
                                for shop,s,k in zip(shops,starts,kits)])
            reserve = np.where(reserve<=cash,reserve,0)
            if rng is not None:
                # Rifle choice is sampled once per recipient, independent of the number of payers.
                guns = guns + rng.gumbel(size=guns.shape)
                kits = kits + rng.gumbel(size=kits.shape)
            needed = np.flatnonzero(mask & ~carried).tolist()
            edges = fund_primaries(needed,cash-reserve,shops,human,guns,pay)
            if edges is None and reserve.any():
                edges = fund_primaries(needed,cash,shops,human,guns,pay)
            if edges is not None:
                break
        else:
            raise ValueError("Existing lineup must be feasible")
        balances = cash.copy()
        for a,b,item in edges:
            balances[a] -= shops[a][item]
        # Best-supported accessories first; the empty basket is always feasible.
        for b in np.argsort(-kits.max(axis=1)).tolist():
            reserve[b] = 0
            for j in np.argsort(-kits[b]):
                kit = self.kits[j]
                if not can_add_kit(starts[b],kit):
                    continue
                funded = fund_kit(b,kit,balances,reserve,shops,human,pay)
                if funded is not None:
                    balances,extra = funded
                    edges.extend(extra)
                    break
        return edges,balances
