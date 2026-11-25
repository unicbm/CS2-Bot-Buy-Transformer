using System.Numerics;
using static BotBuyPatch.TeamNetwork;

namespace BotBuyPatch;

internal sealed partial class TeamPlanner
{
    private sealed record Heads(double[][] Guns, double[][] Kits, double[][][] Pay, int[] Reserve);
    private sealed record Proposal(int[][] Edges, int[] Balances);
    private sealed record Option(int Payer, int Item, int Price, double Score);
    private sealed record Beam(double Score, int[] Cash, int[][] Edges);

    private Heads Shopping(double[][] h, int mask, Player[] t)
    {
        int count = BitOperations.PopCount((uint)mask);
        var z = Five.Select(i => Relu(net.Linear("recipient.0", [.. h[i], (mask >> (4 - i)) & 1, count / 5d], row: i))).ToArray();
        var guns = z.Select((v, i) => FloatLogSoftmax(net.Linear("weapon", v, row: i))).ToArray();
        var kits = z.Select((v, i) => FloatLogSoftmax(net.Linear("kit", v, row: i))).ToArray();
        var reserve = new int[5];
        var pay = new double[5][][];
        foreach (int b in Five)
        {
            if (!t[b].ReplayDonor && t[b].Start[2] == 0 && kits[b].Select((v, j) => data.Kits[j].Any(n => n is 50 or 51) ? Math.Exp(v) : 0).Sum() > .5)
            {
                int price = t[b].Shop.Where(p => p.Key is 50 or 51).Select(p => p.Value).DefaultIfEmpty().Min();
                if (price <= t[b].Cash) reserve[b] = price;
            }
            var pairs = Five.Select(a => net.Linear("payer.2", Relu(net.Linear("payer.0", [.. z[b], .. z[a], a == b ? 1 : 0])))).ToArray();
            pay[b] = Enumerable.Range(0, 3).Select(group => FloatLogSoftmax(Five.Select(a => pairs[a][group]).ToArray())).ToArray();
        }
        return new(guns, kits, pay, reserve);
    }

    private static int[][]? FundGuns(Player[] t, int[] needed, int[] cash, double[][] guns, double[][][] pay)
    {
        var options = new List<Option>[5];
        foreach (int b in needed)
        {
            options[b] = [];
            for (int j = 0; j < Guns.Length; j++)
            {
                int item = Guns[j];
                if (!t[b].Shop.TryGetValue(item, out int price)) continue;
                foreach (int a in Five)
                    if ((!t[a].ReplayDonor || t[b].Human) && (a == b || !t[a].Human) && cash[a] >= price && t[a].Shop.GetValueOrDefault(item, -1) == price)
                        options[b].Add(new(a, item, price, guns[b][j] + pay[b][0][a]));
            }
            if (options[b].Count == 0) return null;
        }
        Beam[] beam = [new(0, cash, [])];
        foreach (int b in needed.OrderBy(b => options[b].Count))
        {
            var next = new Dictionary<(int, int, int, int, int), Beam>();
            foreach (var node in beam)
                foreach (var opt in options[b])
                {
                    int a = opt.Payer;
                    if (node.Cash[a] < opt.Price) continue;
                    var balances = (int[])node.Cash.Clone(); balances[a] -= opt.Price;
                    var key = (balances[0], balances[1], balances[2], balances[3], balances[4]);
                    double score = node.Score + opt.Score;
                    if (!next.TryGetValue(key, out var previous) || score > previous.Score)
                        next[key] = new(score, balances, [.. node.Edges, [a, b, opt.Item]]);
                }
            if (next.Count == 0) return null;
            beam = next.Values.OrderByDescending(n => n.Score).Take(24).ToArray();
        }
        return beam[0].Edges;
    }
    private static bool FundKit(Player[] t, int b, int[] kit, int[] balances, int[] reserve, double[][] pay, List<int[]> edges)
    {
        var remaining = (int[])balances.Clone();
        List<int[]> extra = [];
        foreach (int item in kit.OrderBy(Portable))
        {
            if (!t[b].Shop.TryGetValue(item, out int price)) return false;
            int best = -1, group = Pistols.Contains(item) ? 1 : 2;
            foreach (int a in Five)
            {
                if ((a != b && !CanSponsor(t[a], t[b], item, price)) ||
                    remaining[a] - (a == b ? 0 : reserve[a]) < price) continue;
                if (best < 0 || (t[a].ReplayDonor != t[best].ReplayDonor ? t[a].ReplayDonor : pay[group][a] > pay[group][best]))
                    best = a;
            }
            if (best < 0) return false;
            remaining[best] -= price; extra.Add([best, b, item]);
        }
        remaining.CopyTo(balances, 0); edges.AddRange(extra); return true;
    }
    private static double Gumbel(Func<double> uniform)
    {
        double u;
        do { u = 1 - uniform(); } while (u == 1);
        return -Math.Log(-Math.Log(u));
    }
    private Proposal Propose(Player[] t, double[][] h, double[] scores, int carried, Dictionary<int, Heads> cache, Func<double> uniform)
    {
        var cash = t.Select(p => p.Cash).ToArray();
        var priorities = scores.Select(v => v + Gumbel(uniform)).ToArray();
        int lockedEmpty = Five.Sum(i => t[i].ReplayDonor && t[i].Start[0] == 0 ? 1 << (4-i) : 0);
        foreach (int mask in Enumerable.Range(0, 32).Where(m => (m & carried) == carried && (m & lockedEmpty) == 0).OrderByDescending(m => priorities[m]))
        {
            if (!cache.TryGetValue(mask, out var head)) cache[mask] = head = Shopping(h, mask, t);
            double[][] Perturb(double[][] logits) => logits.Select(row => row.Select(v => v + Gumbel(uniform)).ToArray()).ToArray();
            var guns = Perturb(head.Guns); var kits = Perturb(head.Kits);
            var reserve = (int[])head.Reserve.Clone();
            var needed = Five.Where(b => ((mask & ~carried) >> (4 - b) & 1) != 0).ToArray();
            var primary = FundGuns(t, needed, Five.Select(i => cash[i] - reserve[i]).ToArray(), guns, head.Pay);
            if (primary == null && reserve.Any(v => v != 0)) primary = FundGuns(t, needed, cash, guns, head.Pay);
            if (primary == null) continue;
            var balances = (int[])cash.Clone();
            foreach (var e in primary) balances[e[0]] -= t[e[0]].Shop[e[2]];
            if (t.Any(p => p.ReplayDonor)) SponsorHumanPurchases(t, primary, balances, replayOnly: true);
            var edges = primary.ToList();
            foreach (int b in Five.Where(i => !t[i].ReplayDonor).OrderByDescending(i => kits[i].Max()))
            {
                reserve[b] = 0;
                foreach (int j in Enumerable.Range(0, kits[b].Length).OrderByDescending(j => kits[b][j]))
                    if (CanAdd(t[b].Start, data.Kits[j]) && FundKit(t, b, data.Kits[j], balances, reserve, head.Pay[b], edges)) break;
            }
            return new(edges.ToArray(), balances);
        }
        throw new InvalidDataException("No feasible lineup for carried inventory");
    }
    // Counts of T-side AK recipients leaving spawn without armor, with vest only, or with helmet.
    private static int[] AkArmorCounts(Player[] team, Proposal proposal)
    {
        var counts = new int[3];
        foreach (int player in Five)
        {
            var inventory = team[player].Start;
            bool ak = inventory[0] == 7, armor = inventory[2] > 0, helmet = inventory[3] != 0;
            foreach (var edge in proposal.Edges)
            {
                if (edge[1] != player) continue;
                ak |= edge[2] == 7;
                armor |= edge[2] is 50 or 51;
                helmet |= edge[2] == 51;
            }
            if (ak) counts[armor ? (helmet ? 2 : 1) : 0]++;
        }
        return counts;
    }

    private int[][] ChooseDeparture(Player[] t, Func<double> uniform)
    {
        var x = Context(t, data.Roles); var h = net.Encode("project", x);
        var count = FloatLogSoftmax(net.Linear("count", Mean(h)));
        var slots = Five.Select(i =>
        {
            double personal = net.Linear("personal.2", Relu(net.Linear("personal.0", x[i])))[0];
            return net.Linear("assignment", h[i]).Select(v => v + personal).ToArray();
        }).ToArray();
        var scores = Enumerable.Range(0, 32).Select(mask =>
        {
            int n = BitOperations.PopCount((uint)mask);
            return count[n] + Five.Sum(b => ((mask >> (4 - b)) & 1) * slots[b][n]);
        }).ToArray();
        var wallet = x.Select(row => net.Linear("wallet.2", Relu(net.Linear("wallet.0", row)))).ToArray();
        int carried = Five.Sum(b => t[b].Start[0] == 0 ? 0 : 1 << (4 - b));
        var cache = new Dictionary<int, Heads>();
        var proposals = Enumerable.Range(0, 4).Select(_ => Propose(t, h, scores, carried, cache, uniform)).ToArray();
        var likelihood = proposals.Select(p => -Five.Sum(i => Math.Abs(p.Balances[i] / 1000d -
            Math.Min(Softplus(wallet[i][0]), x[i][0] * 5)) / (.1 + Softplus(wallet[i][1])))).ToArray();
        if (t[0].Side == 2 && data.TerroristAkArmorLogWeights is { } weights)
        {
            var counts = proposals.Select(p => AkArmorCounts(t, p)).ToArray();
            // A gift-rate correction is relevant only when the AK calibration can change the choice.
            bool varies = counts.Skip(1).Any(c => !c.SequenceEqual(counts[0]));
            if (varies)
            {
                for (int i = 0; i < proposals.Length; i++)
                {
                    var c = counts[i];
                    int gifts = proposals[i].Edges.Count(e => e[0] != e[1]);
                    likelihood[i] += c[0] * weights[0] + c[1] * weights[1] + c[2] * weights[2] + gifts * weights[3];
                }
            }
        }
        double max = likelihood.Max();
        return proposals[Sample(likelihood.Select(v => Math.Exp(v - max)).ToArray(), uniform)].Edges;
    }
}
