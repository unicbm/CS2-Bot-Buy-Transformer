using System.IO.Compression;
using System.Text.Json;
using static BotBuyPatch.TeamNetwork;

namespace BotBuyPatch;

internal sealed record OpeningTemplate(int Side, bool Pistol, bool Overtime, double[][] X, int[][] Edges);
internal sealed record TeamWeights(Dictionary<string, double[]> Departure, Dictionary<string, double[]> Opening,
    Dictionary<string, int> Roles, Dictionary<string, int> OpeningRoles, Dictionary<int, Dictionary<int, int>> Menu,
    Dictionary<int, Dictionary<int, int>> OpeningMenu, int[][] Kits, OpeningTemplate[] Bank, double[]? TerroristAkArmorLogWeights);
internal sealed record TeamReply(bool Covered, string[] Players, int[][] Transactions);

internal sealed partial class TeamPlanner(TeamWeights data)
{
    private readonly TeamNetwork net = new(data.Departure), opening = new(data.Opening);
    private static readonly int[] Guns = [7, 8, 9, 10, 11, 13, 14, 16, 17, 19, 23, 24, 25, 26, 27, 28, 29, 33, 34, 35, 38, 39, 40, 60];
    private static readonly int[] Pistols = [1, 2, 3, 4, 30, 32, 36, 61, 63, 64];
    private static readonly int[] Five = [0, 1, 2, 3, 4];
    internal static bool Portable(int item) => item is not (50 or 51 or 55);
    private sealed record Player(string Id, string Name, int Cash, bool Human, bool ReplayDonor, int Side, int Round, bool OT,
        int[] Start, double[] Features, Dictionary<int, int> Shop, Dictionary<int, int> OpeningShop);
    internal static TeamPlanner Load(string path)
    {
        using var file = File.OpenRead(path);
        using var stream = new GZipStream(file, CompressionMode.Decompress);
        var model = JsonSerializer.Deserialize<TeamWeights>(stream) ?? throw new InvalidDataException("Missing team weights");
        if (model.TerroristAkArmorLogWeights is { } w && (w.Length != 4 || !w.All(double.IsFinite)))
            throw new InvalidDataException("Invalid AK armor calibration");
        return new(model);
    }
    private Player Parse(JsonElement r)
    {
        double Num(string k) => r.GetProperty(k).ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.False => 0,
            JsonValueKind.True => 1,
            _ => r.GetProperty(k).GetDouble()
        };
        int side = (int)Num("side"), round = (int)Num("round");
        var before = r.GetProperty("before");
        int[] ids = before[0].Deserialize<int[]>()!;
        int Held(int[] group) { var held = ids.Where(group.Contains).ToArray(); return held.Length switch { 0 => 0, 1 => held[0], _ => -1 }; }
        int Flag(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : Convert.ToInt32(e.GetBoolean());
        int[] start = [Held(Guns),Held(Pistols),before[1].GetInt32(),Flag(before[2]),Flag(before[3]),ids.Contains(31)?1:0,
            .. Enumerable.Range(43,6).Select(item=>ids.Count(id=>id==item))];
        Dictionary<int, int> Menu(Dictionary<int, Dictionary<int, int>> menus)
        {
            if (r.TryGetProperty("offers", out var offers)) return offers.EnumerateObject().Where(p => p.Value.GetInt32() >= 0)
                .ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32());
            var result = new Dictionary<int, int>(menus[side]);
            if (result.ContainsKey(51)) result[51] = start[3] != 0 ? 650 : start[2] == 100 ? 350 : 1000;
            return result;
        }
        double[] features = [Num("cash")/5000,Num("team_cash")/5000,Num("team_primaries")/5,side==3?1:0,start[2]/100d,
            start[3],start[4],start[5],round is 1 or 13?1:0,round is 12 or 24?1:0,Num("own_score")/12,Num("opp_score")/12,
            Num("own_point"),Num("opp_point"),Num("overtime"),Num("ot_half_round")/3,Num("ot_reset_cash")/10000,
            ..start.Skip(6).Select(v=>(double)v),..Guns.Select(v=>v==start[0]?1d:0),..Pistols.Select(v=>v==start[1]?1d:0)];
        return new(r.GetProperty("player").GetString()!, r.GetProperty("name").GetString()!.Trim().ToLowerInvariant(),
            (int)Num("cash"), r.TryGetProperty("human", out var human) && human.GetBoolean(), r.TryGetProperty("replay_donor", out var replay) && replay.GetBoolean(), side, round, Num("overtime") != 0, start,
            features, Menu(data.Menu), Menu(data.OpeningMenu));
    }
    private static double[][] Context(Player[] t, Dictionary<string, int> roles) => t.Select(p =>
    {
        int role = roles.GetValueOrDefault(p.Name, -1);
        return p.Features.Select(v => (double)(float)v).Concat(Enumerable.Range(0, 4).Select(i => i == role ? 1d : 0)).ToArray();
    }).ToArray();

    internal TeamReply Decide(string request, Func<double>? uniform = null)
    {
        uniform ??= Random.Shared.NextDouble;
        using var doc = JsonDocument.Parse(request);
        var t = doc.RootElement.EnumerateArray().Select(Parse).OrderBy(p => p.Cash)
            .ThenBy(p => p.Start, Comparer<int[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (t.Length != 5 || t.Select(p => p.Id).Distinct().Count() != 5 || t.Any(p => p.Side != t[0].Side || p.Round != t[0].Round))
            throw new InvalidDataException("Expected five teammates");
        int[][]? edges = null;
        if (!t.Any(p => p.ReplayDonor) && t[0].Round is 1 or 13 && t.All(p => p.Cash == 800 && p.Start[0] == 0 && p.Start[2] == 0))
            edges = ChooseOpening(t, uniform);
        edges ??= ChooseDeparture(t, uniform);
        // Opening templates are shared. Return fresh receipts before changing any payer.
        var receipts = edges.Select(e => new[] { e[0], e[1], e[2], t[e[1]].Shop[e[2]] }).ToArray();
        if (t.Any(p => p.Human))
        {
            var remaining = t.Select(p => p.Cash).ToArray();
            foreach (var e in receipts) remaining[e[0]] -= e[3];
            SponsorHumanPurchases(t, receipts, remaining);
        }
        return new(true, t.Select(p => p.Id).ToArray(), receipts);
    }
    private static bool CanSponsor(Player donor, Player receiver, int item, int price) =>
        !donor.Human && (!donor.ReplayDonor || receiver.Human) &&
        (Portable(item) ? donor.Shop.GetValueOrDefault(item, -1) == price : receiver.Human);

    private static void SponsorHumanPurchases(Player[] team, int[][] edges, int[] cash, bool replayOnly = false)
    {
        foreach (var e in edges.Where(e => e[0] == e[1] && team[e[1]].Human)
                     .OrderByDescending(e => team[e[1]].Shop[e[2]]))
        {
            int receiver = e[1], item = e[2], price = team[receiver].Shop[item];
            int donor = Five.Where(a => (!replayOnly || team[a].ReplayDonor) && cash[a] >= price &&
                CanSponsor(team[a], team[receiver], item, price)).OrderByDescending(a => team[a].ReplayDonor)
                .ThenByDescending(a => cash[a]).FirstOrDefault(-1);
            if (donor < 0) continue;
            cash[donor] -= price;
            cash[receiver] += price;
            e[0] = donor;
        }
    }

    private static int Sample(double[] probabilities, Func<double> uniform)
    {
        double value = uniform() * probabilities.Sum(), cum = 0;
        for (int i = 0; i < probabilities.Length - 1; i++) { cum += probabilities[i]; if (value < cum) return i; }
        return probabilities.Length - 1;
    }
    private static bool CanAdd(int[] state, int[] items)
    {
        var grenades = state[6..];
        foreach (int item in items)
        {
            if (Pistols.Contains(item) && (state[1] is not (0 or 4 or 32 or 61) || item == state[1])) return false;
            if (item == 55 && state[4] != 0 || item == 31 && state[5] != 0) return false;
            if (state[2] == 100 && (item == 50 || item == 51 && state[3] != 0)) return false;
            if (item is >= 43 and <= 48 && ++grenades[item - 43] > (item == 43 ? 2 : 1)) return false;
        }
        return grenades.Sum() <= 4;
    }
}
