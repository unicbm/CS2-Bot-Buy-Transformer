"""The pistol-opening policy used for five standard $800 starts."""
import itertools
from collections import defaultdict

import numpy as np
import torch
from torch import nn
from economy import ROOT, PRIMARY, PISTOLS, apply_plan, context, prices_for

MIXES=np.array([m for m in itertools.product(range(6),repeat=4) if sum(m)==5])

class Planner:
    """Rank intact observed transactions against all five current inventories."""
    def __init__(self,roles,bank,menu):
        self.roles,self.menu = roles,menu
        self.bank = bank
        self.contexts = np.stack([e["x"] for e in bank])
        self.groups = defaultdict(list)
        for j,e in enumerate(bank):
            self.groups[tuple(e["group"])].append(j)

    def candidates(self,team):
        x = context(team,self.roles)
        r = team[0]
        ids = self.groups[(r["side"],r["round"] in (1,13),bool(r["overtime"]))]
        offers = [prices_for(row,self.menu) for row in team]
        # Team slots are aligned by starting cash and inventory in both corpora.
        if not ids:
            return []
        dist = ((self.contexts[ids]-x)**2).sum(axis=(1,2))
        rank = np.argsort(dist,kind="stable")
        scored = []
        for pos in rank:
            index = ids[pos]
            e = self.bank[index]
            edges = e["edges"]
            plan = apply_plan(team,edges,offers)
            if plan is None:
                continue
            scored.append((-float(dist[pos])/5,index,plan))
            if len(scored)>=24:
                break
        return scored



def softmax(values,axis=None):
    exp=np.exp(values-np.max(values,axis=axis,keepdims=True))
    return exp/exp.sum(axis=axis,keepdims=True)


def mix(states):
    return np.bincount([2*int(s[0]!=0)+int(s[2]>0) for s in states],minlength=4)


class TeamBudget(nn.Module):
    def __init__(self,n):
        super().__init__()
        self.player=nn.Linear(n,32)
        self.encoder=nn.TransformerEncoderLayer(32,4,64,dropout=.05,batch_first=True)
        self.composition=nn.Linear(32,len(MIXES))
        self.budget=nn.Sequential(nn.Linear(36,32),nn.ReLU(),nn.Linear(32,2))
        self.register_buffer("mixes",torch.tensor(MIXES/5,dtype=torch.float32),persistent=False)

    def forward(self,x,cash):
        h=self.encoder(self.player(x)).mean(1)
        m=self.mixes.expand(len(h),-1,-1)
        z=self.budget(torch.cat([h[:,None].expand(-1,len(MIXES),-1),m],dim=2))
        return self.composition(h),cash[:,None]*z[:,:,0].sigmoid(),.25+nn.functional.softplus(z[:,:,1])


class CoordinatedPlanner:
    def __init__(self,base,model):
        self.base,self.model,self.menu=base,model,base.menu

    @classmethod
    def load(cls,base):
        state=torch.load(ROOT/"models/pistol-budget.pt",weights_only=True)
        model=TeamBudget(state["n"])
        model.load_state_dict(state["state"])
        return cls(base,model.eval())

    @torch.inference_mode()
    def distribution(self,team,candidates):
        x=torch.from_numpy(context(team,self.base.roles))[None]
        cash=torch.tensor([sum(p["cash"] for p in team)/1000])
        logits,loc,scale=(t[0].numpy() for t in self.model(x,cash))
        # Project each possible team intent to the closest feasible headcounts.
        # Within that set use the predicted budget and the pre-buy distance prior.
        compositions=np.stack([mix(plan["inventory"]) for _,_,plan in candidates])
        dist=np.abs(MIXES[:,None]-compositions).sum(2)
        spend=np.array([sum(tx[3] for tx in plan["transactions"])/1000 for _,_,plan in candidates])
        scores=np.array([s for s,_,_ in candidates])-np.abs(spend-loc[:,None])/scale[:,None]
        scores[dist!=dist.min(1,keepdims=True)]=-np.inf
        return softmax(logits.astype(float)) @ softmax(scores,axis=1)

    def choose(self,team,rng=None,*,complete_rifles=True):
        candidates=self.base.candidates(team)
        if not candidates:
            if not complete_rifles:
                return [],False
            empty=apply_plan(team,[],[prices_for(p,self.menu) for p in team])
            edges=complete_rifle_buys(team,[],empty,self.menu)
            return edges,bool(edges)
        prob=self.distribution(team,candidates)
        k=prob.argmax() if rng is None else rng.choice(len(prob),p=prob/prob.sum())
        _,index,plan=candidates[k]
        edges=self.base.bank[index]["edges"]
        if not complete_rifles:
            return edges,True
        return complete_rifle_buys(team,edges,plan,self.menu),True


def complete_rifle_buys(team,edges,plan,menu):
    """Adapt an observed lineup to today's missing rifles, loadouts and balances."""
    offers=[prices_for(p,menu) for p in team]
    rifles=(7,13) if team[0]["side"]==2 else (60,16,10)
    armed=sum(bool(s[0]) for s in plan["inventory"])
    result=list(edges)
    for b,state in enumerate(plan["inventory"]):
        if state[0]:
            continue
        sidearms=[e for e in result if e[1]==b and e[2] in PISTOLS]
        money=plan["cash"][b]
        # A complete lineup must not leave a solvent player without a primary.
        if armed!=4 and money<4000:
            continue
        without_pistol=[e for e in result if e not in sidearms]
        staged=apply_plan(team,without_pistol,offers)
        balances=staged["cash"]
        armor=0 if state[2] else offers[b].get(50,np.inf)
        # Already armed bot donors can cover the rifle while its recipient buys armor.
        donors=sorted((a for a in range(5) if a!=b and staged["inventory"][a][0]
                       and not team[a].get("human",False)),key=lambda a:-balances[a])
        chosen=None
        for a in [b,*donors]:
            for rifle in rifles:
                price=offers[a].get(rifle)
                if price is None or offers[b].get(rifle)!=price or balances[a]<price:
                    continue
                own_cash=balances[b]-(price if a==b else 0)
                # Prefer an armored rifle, then a service rifle, then self-payment.
                score=(own_cash>=armor,rifle!=rifles[-1],a==b)
                if chosen is None or score>chosen[0]:
                    chosen=(score,a,rifle,own_cash)
        if chosen is None:
            continue
        _,a,rifle,own_cash=chosen
        result=without_pistol+[(a,b,rifle)]
        if not state[2]:
            for item in (51,50):
                if item in offers[b] and own_cash>=offers[b][item]:
                    result.append((b,b,item))
                    break
    # Removing a donated pistol may have refunded *another* player's budget.
    # Check the resulting balances, including nonstandard native loadouts.
    final=apply_plan(team,result,offers)
    for b,(inventory,cash,shop) in enumerate(zip(final["inventory"],final["cash"],offers)):
        if inventory[0] or cash<4000:
            continue
        affordable=[i for i in PRIMARY if i in shop and 0<shop[i]<=cash]
        if affordable:
            weapon=next((i for i in rifles if i in affordable),max(affordable,key=shop.get))
            result.append((b,b,weapon))
    return result
