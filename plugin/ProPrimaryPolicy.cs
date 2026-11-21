using System.Text.Json;

namespace BotBuyPatch;

internal sealed class ProPrimaryPolicy
{
    public int Version { get; init; }
    public string[] Features { get; init; } = [];
    public string[] Actions { get; init; } = [];
    public Dictionary<string, int> Names { get; init; } = new();
    public Dictionary<string, Node[]> Teams { get; init; } = new();

    internal sealed class Node
    {
        public int Feature { get; init; }
        public double Threshold { get; init; }
        public int Left { get; init; }
        public int Right { get; init; }
        public double[]? Prob { get; init; }
        public double[][] Styles { get; init; } = [];
    }

    internal static ProPrimaryPolicy Load(string path)
    {
        var model = JsonSerializer.Deserialize<ProPrimaryPolicy>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        if (model == null || model.Version != 1 || model.Actions.FirstOrDefault() != "none" ||
            !model.Features.SequenceEqual(new[] { "cash", "team_cash", "team_primaries", "armor", "last_round" }))
            throw new InvalidDataException("Unsupported professional primary-buy model.");
        foreach (var side in new[] { "2", "3" })
        {
            var tree = model.Teams[side];
            if (tree.Length == 0) throw new InvalidDataException("Empty primary-buy tree.");
            for (int i = 0; i < tree.Length; i++)
            {
                var node = tree[i];
                if (node.Prob == null)
                {
                    if (node.Left <= i || node.Right <= i || node.Left >= tree.Length || node.Right >= tree.Length ||
                        node.Feature < 0 || node.Feature >= model.Features.Length || !double.IsFinite(node.Threshold))
                        throw new InvalidDataException("Invalid primary-buy branch.");
                }
                else if (node.Styles.Length != 4 || node.Styles.Append(node.Prob).Any(p =>
                    p.Length != model.Actions.Length || p.Any(x => !double.IsFinite(x) || x < 0) ||
                    Math.Abs(p.Sum() - 1) > 1e-6))
                    throw new InvalidDataException("Invalid primary-buy probabilities.");
            }
        }
        if (model.Names.Values.Any(i => i is < 0 or > 3)) throw new InvalidDataException("Unknown buying style.");
        return model;
    }

    internal double[] Probabilities(string side, double[] input, string name)
    {
        var tree = Teams[side];
        var node = tree[0];
        while (node.Prob == null)
            node = tree[input[node.Feature] <= node.Threshold ? node.Left : node.Right];
        return Names.TryGetValue(name.Trim().ToLowerInvariant(), out int style) ? node.Styles[style] : node.Prob;
    }

    internal string? Pick(string side, double[] input, string name, Func<string, bool> available)
        => Sample(Actions, Probabilities(side, input, name), available);

    internal static string? Sample(string[] actions, double[] p, Func<string, bool> available)
    {
        var choices = new List<(string Item, double Weight)>();
        double total = p[0];
        for (int i = 1; i < actions.Length; i++)
        {
            string item = "weapon_" + actions[i];
            if (p[i] > 0 && available(item))
            {
                choices.Add((item, p[i]));
                total += p[i];
            }
        }
        double roll = Random.Shared.NextDouble() * total;
        foreach (var (item, weight) in choices)
        {
            roll -= weight;
            if (roll < 0) return item;
        }
        return null;
    }
}
