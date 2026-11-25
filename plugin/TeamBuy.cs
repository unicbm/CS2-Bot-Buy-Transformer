using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Attributes.Registration;

namespace BotBuyPatch;

public sealed partial class BotBuyPatch
{
    private bool _teamMode;
    private TeamPlanner? _planner;
    private int _humanAdvice;
    private readonly HashSet<CsTeam> _replayWaiting = [];
    private bool _replayWakeQueued;
    private void WaitForReplay(CsTeam side)
    {
        _replayWaiting.Add(side);
        if (_replayWakeQueued) return;
        _replayWakeQueued = true;
        int round = _round;
        AddTimer(.15f, () =>
        {
            if (round != _round) return;
            _replayWakeQueued = false;
            QueuePurchases();
        });
    }

    private void ConfigureLocalPlanner()
    {
        _planner = TeamPlanner.Load(Path.Combine(ModuleDirectory, "team-policy.json.gz"));
        _teamMode = true;
    }

    private static bool TeamParticipant(CCSPlayerController p) => p.IsValid && (HasBotIdentity(p) ? IsAutonomousBot(p)
        : IsUsablePlayer(p) && !p.IsHLTV && !p.ControllingBot);

    private static string BuyLabel(string name) => name switch
    {
        "item_kevlar"=>"防弹衣", "item_assaultsuit"=>"防弹衣＋头盔", "item_defuser"=>"拆弹器",
        "weapon_smokegrenade"=>"烟雾弹", "weapon_flashbang"=>"闪光弹", "weapon_hegrenade"=>"高爆手雷",
        "weapon_incgrenade"=>"燃烧弹", "weapon_molotov"=>"燃烧瓶", "weapon_decoy"=>"诱饵弹",
        "weapon_ak47"=>"AK-47", "weapon_m4a1"=>"M4A4", "weapon_m4a1_silencer"=>"M4A1-S",
        _=>name.Replace("weapon_","").ToUpperInvariant()
    };
    private static string ItemList(IEnumerable<string> names) => string.Join("、",
        names.GroupBy(n => n).Select(g => BuyLabel(g.Key) + (g.Count() > 1 ? $"×{g.Count()}" : "")));
    private readonly HashSet<CsTeam> _teamClaimed = [];
    private readonly HashSet<CsTeam> _teamTried = [];
    private readonly Dictionary<CsTeam,int> _otBank = [];
    private int _teamPlans, _directGifts, _teamEpoch;
    private string _teamLast = "none yet";

    [ConsoleCommand("css_bot_team_buy", "Professional five-player team purchases: 0/1 or status")]
    public void TeamMode(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount == 2 && command.GetArg(1) is "0" or "1")
        {
            bool enabled = command.GetArg(1) == "1";
            if (enabled != _teamMode) { _teamEpoch++; _teamMode = enabled;  }
        }
        else if (command.ArgCount != 1) { command.ReplyToCommand("Usage: css_bot_team_buy [0|1]"); return; }
        command.ReplyToCommand($"[BotBuy] Team={_teamMode} plans={_teamPlans} direct_gifts={_directGifts} advice={_humanAdvice} planner=managed last={_teamLast}");
    }

    private static int[] ItemIds(CCSPlayerController p)
    {
        var services = p.PlayerPawn.Value!.WeaponServices!;
        var ids = services.MyWeapons.Where(h => h.Value is { IsValid:true })
            .Select(h => (int)h.Value!.AttributeManager.Item.ItemDefinitionIndex).ToList();
        if (ids.Count(i => i == 43) == 1 && services.Ammo[14] == 2) ids.Add(43);
        ids.Sort();
        return ids.ToArray();
    }

    private static readonly string[] Shop = PrimaryWeapons.Concat(UtilityWeapons).Concat(new[] {
        "weapon_deagle","weapon_elite","weapon_fiveseven","weapon_glock","weapon_tec9","weapon_hkp2000",
        "weapon_p250","weapon_usp_silencer","weapon_cz75a","weapon_revolver","weapon_taser",
        "item_kevlar","item_assaultsuit","item_defuser" }).ToArray();

    private static int GiftQuantity(CCSPlayerController p, int item) => item switch
    {
        50 => p.PlayerPawn.Value!.ArmorValue == 100 ? 1 : 0,
        51 => p.PlayerPawn.Value!.ArmorValue == 100 && HasHelmet(p) ? 1 : 0,
        55 => HasDefuser(p) ? 1 : 0,
        _ => ItemIds(p).Count(id => id == item)
    };

    private bool BeginTeamPurchase(CsTeam side, List<CCSPlayerController> members)
    {
        if (!_teamMode || _planner==null || members.Count != 5 || _teamTried.Contains(side) || _native == null ||
            ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() != 24) return false;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        var replay = members.Select(IsDtrOwned).ToArray();
        bool replayAssist = replay.Any(v => v);
        if (replayAssist && (!members.Any(p => !HasBotIdentity(p)) ||
            members.Where((p,i) => replay[i]).Any(p => !HasBotIdentity(p) || _processedPawns.Contains(p.PlayerPawn.Raw)))) return false;
        if (rules == null || (!rules.FreezePeriod && !replayAssist) || members.Any(p=>!TeamParticipant(p) || p.InGameMoneyServices == null || !_native.IsBuyAllowed(p)) ||
            PlayerControllers().Count(p=>!p.IsHLTV && p.Team==side)!=5) return false;
        if (members.Where((p,i) => replay[i]).Any(p => !DtrBuyEvidence.Started(p.Slot)))
        {
            WaitForReplay(side);
            return true;
        }
        int round = rules.TotalRoundsPlayed + 1;
        var scores = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager")
            .Where(t => t.TeamNum is 2 or 3).ToDictionary(t=>(int)t.TeamNum,t=>t.Score);
        if (!scores.TryGetValue((int)side,out int own) || !scores.TryGetValue(5-(int)side,out int opp) ||
            own+opp!=round-1) { _teamLast = "missing pre-buy score"; return false; }
        int ot = round>24 ? 1 : 0, phase = ot==1 ? (round-25)%3+1 : 0;
        int target = ot==1 ? 16+3*((round-25)/6) : 13;
        var cash = members.Select(p=>p.InGameMoneyServices!.Account).ToArray();
        if (phase==1 || ot==0) _otBank.Remove(side);
        if (phase==1 && cash.Distinct().Count()==1) _otBank[side]=cash[0];
        var ids = members.Select(ItemIds).ToArray();
        var controllers = members.Select(p=>p.EntityHandle.Raw).ToArray();
        var pawns = members.Select(p=>p.PlayerPawn.Raw).ToArray();
        var inventories = members.Select((p,i)=>new object[]{ids[i],p.PlayerPawn.Value!.ArmorValue,HasHelmet(p),HasDefuser(p)}).ToArray();
        int primaries = members.Count(HasPrimaryWeapon);
        var request = members.Select((p,i)=>new {
            player=controllers[i].ToString(), name=p.PlayerName, human=!HasBotIdentity(p), replay_donor=replay[i], side=(int)side, round, cash=cash[i],
            team_cash=cash.Average(), team_primaries=primaries, own_score=own, opp_score=opp,
            own_point=own==target-1?1:0, opp_point=opp==target-1?1:0, overtime=ot, ot_half_round=phase,
            ot_reset_cash=_otBank.TryGetValue(side,out int bank)?(int?)bank:null, before=inventories[i],
            offers=Shop.Select(n=>_native.Quote(p,n)).Where(q=>q.HasValue)
                .ToDictionary(q=>(int)q!.Value.Definition,q=>q!.Value.Price)
        }).ToArray();
        int epoch=_teamEpoch, lifecycle=_round;
        _teamTried.Add(side);
        _teamClaimed.Add(side);
        _ = RequestPlan(_planner, JsonSerializer.Serialize(request), reply =>
        {
            if (lifecycle!=_round || !_roundOpen || _native==null) return;
            var delivered = new List<(CCSPlayerController From, CCSPlayerController To, string Item, int Cost)>();
            try
            {
                if (!_teamMode || _planner==null || epoch!=_teamEpoch || reply?.Covered!=true || (!rules.FreezePeriod && !replayAssist) ||
                    members.Where((p,i)=>!TeamParticipant(p) || IsDtrOwned(p)!=replay[i] || (replay[i] && !DtrBuyEvidence.Started(p.Slot)) || p.Team!=side || p.PlayerPawn.Raw!=pawns[i] ||
                        p.EntityHandle.Raw!=controllers[i] || p.InGameMoneyServices?.Account!=cash[i] ||
                        !_native.IsBuyAllowed(p) || !ItemIds(p).SequenceEqual(ids[i]) ||
                        p.PlayerPawn.Value!.ArmorValue!=(int)inventories[i][1] ||
                        HasHelmet(p)!=(bool)inventories[i][2] || HasDefuser(p)!=(bool)inventories[i][3]).Any())
                {
                    if (replayAssist && reply?.Covered==true && epoch==_teamEpoch) _teamTried.Remove(side);
                    _teamLast="no current eligible plan";
                    _teamClaimed.Remove(side);
                    QueuePurchases();
                    return;
                }
                var byId = members.ToDictionary(p=>p.EntityHandle.Raw.ToString());
                if (reply.Players.Length!=5 || reply.Players.Distinct().Count()!=5 ||
                    reply.Players.Any(id=>!byId.ContainsKey(id)) || reply.Transactions.Length>64)
                    throw new InvalidDataException("Invalid team reply");
                var ordered = reply.Players.Select(id=>byId[id]).ToArray();
                var remaining = ordered.Select(p=>p.InGameMoneyServices!.Account).ToArray();
                var purchases = new List<(CCSPlayerController From,CCSPlayerController To,string Name,BotBuyNative.Offer Offer)>();
                foreach (var tx in reply.Transactions)
                {
                    if (tx.Length!=4 || (uint)tx[0]>=5 || (uint)tx[1]>=5) throw new InvalidDataException("Invalid transfer");
                    var a=ordered[tx[0]]; var b=ordered[tx[1]];
                    string name=Shop.Single(n=>BotBuyNative.Definition(n)==tx[2]);
                    bool gift = tx[0] != tx[1], portable = TeamPlanner.Portable(tx[2]);
                    var offer=_native.Quote(portable ? a : b,name);
                    if (IsDtrOwned(b) || (IsDtrOwned(a) && HasBotIdentity(b)))
                        throw new InvalidDataException("Replay equipment is owned by DemoTracer");
                    if ((gift && (!HasBotIdentity(a) || (!portable && HasBotIdentity(b)))) || offer is null || offer.Value.Price!=tx[3] || tx[3]<0 ||
                        (remaining[tx[0]]-=tx[3])<0 || (gift && portable && _native.Quote(b,name)?.Price!=tx[3]))
                        throw new InvalidDataException("Stale or unavailable offer");
                    purchases.Add((a,b,name,offer.Value));
                }
                foreach (var member in members.Where(p=>HasBotIdentity(p))) _processedPawns.Add(member.PlayerPawn.Raw);
                foreach (var person in members.Where(p=>!HasBotIdentity(p)))
                {
                    var own=purchases.Where(t=>t.From.EntityHandle.Raw==person.EntityHandle.Raw).ToArray();
                    if (own.Length == 0 && purchases.Any(t => t.To.Slot == person.Slot)) continue;
                    person.PrintToChat(own.Length==0 ? $" {ChatColors.Grey}本回合建议存钱" :
                        $" {ChatColors.Yellow}自购 {ChatColors.Default}{ItemList(own.Select(t=>t.Name))} {ChatColors.Grey}${own.Sum(t=>t.Offer.Price)}");
                    _humanAdvice++;
                }
                int executed=0;
                foreach (var (a,b,name,offer) in purchases)
                {
                    if (!HasBotIdentity(a)) continue;
                    bool gift=a.EntityHandle.Raw!=b.EntityHandle.Raw;
                    int payerCash=a.InGameMoneyServices!.Account, receiverCash=b.InGameMoneyServices!.Account;
                    int held=gift ? GiftQuantity(b,offer.Definition) : 0;
                    int? charged = gift ? _native.Sponsor(a,b,name,offer) : Buy(a,name) ? offer.Price : null;
                    if (charged == null) { _teamLast=$"R{round} partial: {a.PlayerName} -> {b.PlayerName} {name} rejected"; return; }
                    executed++;
                    if (gift)
                    {
                        if (a.InGameMoneyServices.Account != payerCash-charged ||
                            b.InGameMoneyServices.Account != receiverCash ||
                            GiftQuantity(b,offer.Definition) != held+1)
                            throw new InvalidOperationException($"Gift settlement mismatch: {name}");
                        if (!HasBotIdentity(b)) delivered.Add((a, b, name, charged.Value));
                        _directGifts++;
                        Console.WriteLine($"[BotBuy] direct R{round} {a.PlayerName} -> {b.PlayerName} {name} ${charged}; payer {payerCash}->{a.InGameMoneyServices.Account}, recipient {receiverCash}->{b.InGameMoneyServices.Account}, inventory {held}->{held+1}");
                    }
                }
                _teamPlans++;
                _teamLast=$"R{round} {side} {executed} bot purchases / {purchases.Count} planned" + (replayAssist ? " (DTR assist)" : "");
            }
            catch (Exception ex)
            {
                _teamLast=ex.Message;
                Console.WriteLine($"[BotBuy] Team plan stopped: {ex.Message}");
                if (!pawns.Any(_processedPawns.Contains)) { _teamClaimed.Remove(side); QueuePurchases(); }
            }
            finally
            {
                foreach (var gifts in delivered.GroupBy(g => (g.To.Slot, g.From.Slot)))
                {
                    var first = gifts.First();
                    first.To.PrintToChat($" {ChatColors.Green}{first.From.PlayerName} 赠 {ChatColors.Default}{ItemList(gifts.Select(g=>g.Item))} {ChatColors.Grey}${gifts.Sum(g=>g.Cost)}");
                }
            }
        });
        return true;
    }

    private async Task RequestPlan(TeamPlanner planner, string request, Action<TeamReply?> complete)
    {
        TeamReply? reply = null;
        try
        {
            reply = await Task.Run(() => planner.Decide(request)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BotBuy] Team planner failed: {ex}");
        }
        if (ReferenceEquals(planner, _planner))
            Server.NextFrame(() => { if (ReferenceEquals(planner, _planner)) complete(reply); });
    }
}
