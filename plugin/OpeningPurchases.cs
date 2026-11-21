using static BotBuyPatch.TeamNetwork;

namespace BotBuyPatch;

internal sealed partial class TeamPlanner
{
    private sealed record Candidate(int[][] Edges, int[] Mix, double Spend, double Prior);
    private static readonly int[][] Mixes = (from a in Enumerable.Range(0, 6)
                                             from b in Enumerable.Range(0, 6 - a)
                                             from c in Enumerable.Range(0, 6 - a - b)
                                             select new[] { a, b, c, 5 - a - b - c }).ToArray();

    // Match the reference reduction, including ties between equally close templates.
    private static double PairwiseSum(ReadOnlySpan<double> x)
    {
        if (x.Length < 8) { double total = 0; foreach (double v in x) total += v; return total; }
        if (x.Length > 128) { int split = x.Length / 2 / 8 * 8; return PairwiseSum(x[..split]) + PairwiseSum(x[split..]); }
        Span<double> lanes = stackalloc double[8]; x[..8].CopyTo(lanes);
        int i = 8;
        for (; i <= x.Length - 8; i += 8) for (int j = 0; j < 8; j++) lanes[j] += x[i + j];
        double sum = ((lanes[0] + lanes[1]) + (lanes[2] + lanes[3])) + ((lanes[4] + lanes[5]) + (lanes[6] + lanes[7]));
        for (; i < x.Length; i++) sum += x[i];
        return sum;
    }
    private static double Distance(double[][] a, double[][] b)
    {
        Span<double> squares = stackalloc double[5 * 61];
        int n = 0;
        for (int i = 0; i < 5; i++) for (int j = 0; j < 61; j++) { double delta = a[i][j] - b[i][j]; squares[n++] = delta * delta; }
        return PairwiseSum(squares);
    }
    private static Candidate? OpeningCandidate(Player[] t, OpeningTemplate template, double distance)
    {
        var cash = t.Select(p => p.Cash).ToArray();
        var received = Five.Select(_ => new List<int>()).ToArray();
        foreach (var e in template.Edges)
        {
            var (a, b, item) = (e[0], e[1], e[2]);
            if (!t[a].OpeningShop.TryGetValue(item, out int price) || (cash[a] -= price) < 0 ||
                a != b && (!Portable(item) || t[a].Human || t[b].OpeningShop.GetValueOrDefault(item, -1) != price)) return null;
            received[b].Add(item);
        }
        var mix = new int[4];
        foreach (int b in Five)
        {
            var items = received[b];
            if (!CanAdd(t[b].Start, items.ToArray())) return null;
            int armed = t[b].Start[0] != 0 || items.Any(Guns.Contains) ? 2 : 0;
            int armor = t[b].Start[2] > 0 || items.Any(i => i is 50 or 51) ? 1 : 0;
            mix[armed + armor]++;
        }
        return new(template.Edges, mix, (t.Sum(p => p.Cash) - cash.Sum()) / 1000d, -distance / 5);
    }

    private int[][]? ChooseOpening(Player[] t, Func<double> uniform)
    {
        var x = Context(t, data.OpeningRoles);
        var ranked = data.Bank.Where(e => e.Side == t[0].Side && e.Pistol && e.Overtime == t[0].OT)
            .Select(e => (Template: e, Distance: Distance(x, e.X))).OrderBy(e => e.Distance);
        var candidates = ranked.Select(e => OpeningCandidate(t, e.Template, e.Distance)).OfType<Candidate>().Take(24).ToArray();
        if (candidates.Length == 0) return null;
        var h = Mean(opening.Encode("player", x));
        var mixture = LogSoftmax(opening.Linear("composition", h));
        var probabilities = new double[candidates.Length];
        for (int i = 0; i < Mixes.Length; i++)
        {
            var mix = Mixes[i];
            var z = opening.Linear("budget.2", Relu(opening.Linear("budget.0", [.. h, .. mix.Select(n => n / 5d)])));
            double budget = t.Sum(p => p.Cash) / 1000d / (1 + Math.Exp(-z[0])), scale = .25 + Softplus(z[1]);
            int[] distances = candidates.Select(c => Enumerable.Range(0, 4).Sum(j => Math.Abs(mix[j] - c.Mix[j]))).ToArray();
            int min = distances.Min();
            var logits = candidates.Select((c, j) => distances[j] == min ? c.Prior - Math.Abs(c.Spend - budget) / scale : double.NegativeInfinity).ToArray();
            var scores = LogSoftmax(logits);
            for (int j = 0; j < scores.Length; j++) probabilities[j] += Math.Exp(mixture[i] + scores[j]);
        }
        return candidates[Sample(probabilities, uniform)].Edges;
    }
}
