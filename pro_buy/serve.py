"""Local inference service for the BotBuy plugin."""
import argparse
import json
import socketserver

import numpy as np
import torch

from departure import DeparturePlanner
from economy import ROOT, apply_plan, equipment, prices_for
from pistol import CoordinatedPlanner, Planner


def load_planner():
    data = torch.load(ROOT / "models/pistol-policy.pt", weights_only=True)
    opening = Planner(data["roles"], data["bank"], data["menu"])
    return DeparturePlanner.load(CoordinatedPlanner.load(opening))


def decide(planner, team, rng, complete_rifles=False):
    if (len(team) != 5 or len({r["player"] for r in team}) != 5
            or len({(r["side"], r["round"]) for r in team}) != 1):
        raise ValueError("Expected five distinct teammates in the same round")
    for row in team:
        row["start"] = equipment(row["before"])
    team.sort(key=lambda r: (r["cash"], r["start"], r["player"]))
    edges, covered = planner.choose(team, rng, complete_rifles=complete_rifles)
    result = apply_plan(team, edges, [prices_for(r, planner.menu) for r in team])
    if result is None:
        raise ValueError("Infeasible team purchase")
    return dict(**result, covered=covered, players=[r["player"] for r in team],
                policy="departure", rifle_completion=complete_rifles)


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--port", type=int, default=0)
    args = cli.parse_args()
    torch.set_num_threads(2)
    torch.backends.mha.set_fastpath_enabled(False)
    planner = load_planner()
    rng = np.random.default_rng()
    complete_rifles = False

    class Handler(socketserver.StreamRequestHandler):
        def handle(self):
            nonlocal complete_rifles
            self.connection.settimeout(2)
            raw = self.rfile.readline(65537)
            if len(raw) > 65536:
                raise ValueError("Oversized team snapshot")
            body = json.loads(raw)
            if isinstance(body, dict) and set(body) == {"set_rifle_completion"}:
                if not isinstance(body["set_rifle_completion"], bool):
                    raise ValueError("Expected boolean")
                complete_rifles = body["set_rifle_completion"]
                reply = {"rifle_completion": complete_rifles}
            else:
                reply = decide(planner, body, rng, complete_rifles)
            self.wfile.write(json.dumps(reply).encode()+b"\n")

    with socketserver.TCPServer(("127.0.0.1", args.port), Handler) as server:
        print(f"BotBuy team planner ready on 127.0.0.1:{server.server_address[1]}", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
