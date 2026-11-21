using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
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
    private string? _plannerRoot;
    private Process? _planner;
    private volatile int _plannerPort=41286;
    private int _humanAdvice;

    private void ConfigureLocalPlanner()
    {
        _plannerRoot=Path.Combine(ModuleDirectory,"planner");
        _teamMode=true;
        EnsureLocalPlanner();
    }

    private void EnsureLocalPlanner()
    {
        if (!_teamMode || _plannerRoot==null || _planner is { HasExited:false }) return;
        StopLocalPlanner();
        try
        {
            var info=new ProcessStartInfo(Path.Combine(_plannerRoot,"python","python.exe"))
            {
                WorkingDirectory=_plannerRoot, UseShellExecute=false, CreateNoWindow=true,
                RedirectStandardOutput=true
            };
            foreach (string arg in new[]{"-B","-u","pro_buy/serve.py","--port","0"}) info.ArgumentList.Add(arg);
            var worker=new Process { StartInfo=info };
            worker.OutputDataReceived+=(_,e)=>
            {
                const string prefix="BotBuy team planner ready on 127.0.0.1:";
                if (worker==_planner && e.Data?.StartsWith(prefix)==true && int.TryParse(e.Data[prefix.Length..],out int port))
                {
                    _plannerPort=port;
                    Console.WriteLine($"[BotBuy] Coordinated planner ready on {port}.");
                }
            };
            _planner=worker;
            worker.Start();
            worker.BeginOutputReadLine();
        }
        catch (Exception ex) { _planner?.Dispose(); _planner=null; Console.WriteLine($"[BotBuy] Could not start planner: {ex.Message}"); }
    }

    private void StopLocalPlanner()
    {
        var worker=_planner;
        _planner=null;
        if (_plannerRoot!=null) _plannerPort=0;
        if (worker==null) return;
        try { if (!worker.HasExited) worker.Kill(entireProcessTree:true); }
        catch (InvalidOperationException) { /* Already exited. */ }
        finally { worker.Dispose(); }
    }

    private static bool TeamParticipant(CCSPlayerController p) => p.IsValid && (HasBotIdentity(p) ? CanManageBot(p)
        : IsUsablePlayer(p) && !p.IsHLTV && !p.ControllingBot);

    private static string BuyLabel(string name) => name switch
    {
        "item_kevlar"=>"防弹衣", "item_assaultsuit"=>"防弹衣＋头盔", "item_defuser"=>"拆弹器",
        "weapon_smokegrenade"=>"烟雾弹", "weapon_flashbang"=>"闪光弹", "weapon_hegrenade"=>"高爆手雷",
        "weapon_incgrenade"=>"燃烧弹", "weapon_molotov"=>"燃烧瓶", "weapon_decoy"=>"诱饵弹",
        "weapon_m4a1"=>"M4A4", "weapon_m4a1_silencer"=>"M4A1-S",
        _=>name.Replace("weapon_","").ToUpperInvariant()
    };
    private readonly HashSet<CsTeam> _teamClaimed = [];
    private readonly HashSet<CsTeam> _teamTried = [];
    private readonly Dictionary<CsTeam,int> _otBank = [];
    private int _teamPlans, _directGifts, _teamEpoch;
    private string _teamLast = "none yet";
    private static readonly JsonSerializerOptions TeamJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private sealed record TeamReply(bool Covered, string[] Players, int[][] Transactions);

    [ConsoleCommand("css_bot_team_buy", "Professional five-player team purchases: 0/1 or status")]
    public void TeamMode(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount == 2 && command.GetArg(1) is "0" or "1")
        {
            bool enabled = command.GetArg(1) == "1";
            if (enabled != _teamMode) { _teamEpoch++; _teamMode = enabled; if(enabled) EnsureLocalPlanner(); else StopLocalPlanner(); }
        }
        else if (command.ArgCount != 1) { command.ReplyToCommand("Usage: css_bot_team_buy [0|1]"); return; }
        command.ReplyToCommand($"[BotBuy] Team={_teamMode} plans={_teamPlans} direct_gifts={_directGifts} advice={_humanAdvice} planner_port={_plannerPort} last={_teamLast}");
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

    private bool BeginTeamPurchase(CsTeam side, List<CCSPlayerController> members)
    {
        if (!_teamMode || _plannerPort==0 || members.Count != 5 || _teamTried.Contains(side) || _native == null ||
            ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() != 24) return false;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules is not { FreezePeriod:true } || members.Any(p=>!TeamParticipant(p) || p.InGameMoneyServices == null || !_native.IsBuyAllowed(p)) ||
            PlayerControllers().Count(p=>!p.IsHLTV && p.Team==side)!=5) return false;
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
            player=controllers[i].ToString(), name=p.PlayerName, human=!HasBotIdentity(p), side=(int)side, round, cash=cash[i],
            team_cash=cash.Average(), team_primaries=primaries, own_score=own, opp_score=opp,
            own_point=own==target-1?1:0, opp_point=opp==target-1?1:0, overtime=ot, ot_half_round=phase,
            ot_reset_cash=_otBank.TryGetValue(side,out int bank)?(int?)bank:null, before=inventories[i],
            offers=Shop.Select(n=>_native.Quote(p,n)).Where(q=>q.HasValue)
                .ToDictionary(q=>(int)q!.Value.Definition,q=>q!.Value.Price)
        }).ToArray();
        int epoch=_teamEpoch, lifecycle=_round;
        _teamTried.Add(side);
        _teamClaimed.Add(side);
        _ = RequestPlan(JsonSerializer.Serialize(request), reply =>
        {
            if (lifecycle!=_round || !_roundOpen || _native==null) return;
            try
            {
                if (!_teamMode || _plannerPort==0 || epoch!=_teamEpoch || reply?.Covered!=true || !rules.FreezePeriod ||
                    members.Where((p,i)=>!TeamParticipant(p) || p.Team!=side || p.PlayerPawn.Raw!=pawns[i] ||
                        p.EntityHandle.Raw!=controllers[i] || p.InGameMoneyServices?.Account!=cash[i] ||
                        !_native.IsBuyAllowed(p) || !ItemIds(p).SequenceEqual(ids[i]) ||
                        p.PlayerPawn.Value!.ArmorValue!=(int)inventories[i][1] ||
                        HasHelmet(p)!=(bool)inventories[i][2] || HasDefuser(p)!=(bool)inventories[i][3]).Any())
                {
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
                    var offer=_native.Quote(a,name);
                    if ((!HasBotIdentity(a) && a.EntityHandle.Raw!=b.EntityHandle.Raw) || offer is null || offer.Value.Price!=tx[3] || tx[3]<0 ||
                        (remaining[tx[0]]-=tx[3])<0 || (a!=b && (name.StartsWith("item_") || _native.Quote(b,name)?.Price!=tx[3])))
                        throw new InvalidDataException("Stale or unavailable offer");
                    purchases.Add((a,b,name,offer.Value));
                }
                foreach (var member in members.Where(p=>HasBotIdentity(p))) _processedPawns.Add(member.PlayerPawn.Raw);
                foreach (var person in members.Where(p=>!HasBotIdentity(p)))
                {
                    var own=purchases.Where(t=>t.From.EntityHandle.Raw==person.EntityHandle.Raw).ToArray();
                    var items=own.GroupBy(t=>t.Name).Select(g=>BuyLabel(g.Key)+(g.Count()>1?$" ×{g.Count()}":""));
                    person.PrintToChat(own.Length==0 ? "[BotBuy] 本回合建议保留现金。" :
                        $"[BotBuy] 建议自购：{string.Join("、",items)}，共 ${own.Sum(t=>t.Offer.Price)}。");
                    _humanAdvice++;
                }
                int executed=0;
                foreach (var (a,b,name,offer) in purchases)
                {
                    if (!HasBotIdentity(a)) continue;
                    bool gift=a.EntityHandle.Raw!=b.EntityHandle.Raw;
                    int payerCash=a.InGameMoneyServices!.Account, receiverCash=b.InGameMoneyServices!.Account;
                    int held=gift ? ItemIds(b).Count(i=>i==offer.Definition) : 0;
                    bool ok=gift ? _native.Sponsor(a,b,name,offer) : Buy(a,name);
                    if (!ok) { _teamLast=$"R{round} partial: {a.PlayerName} -> {b.PlayerName} {name} rejected"; return; }
                    executed++;
                    if (gift)
                    {
                        if (a.InGameMoneyServices.Account != payerCash-offer.Price ||
                            b.InGameMoneyServices.Account != receiverCash ||
                            ItemIds(b).Count(i=>i==offer.Definition) != held+1)
                            throw new InvalidOperationException($"Gift settlement mismatch: {name}");
                        if (!HasBotIdentity(b)) b.PrintToChat($"[BotBuy] {a.PlayerName} 已为你购买 {BuyLabel(name)}，已到账。");
                        _directGifts++;
                        Console.WriteLine($"[BotBuy] direct R{round} {a.PlayerName} -> {b.PlayerName} {name} ${offer.Price}; payer {payerCash}->{a.InGameMoneyServices.Account}, recipient {receiverCash}->{b.InGameMoneyServices.Account}, inventory {held}->{held+1}");
                    }
                }
                _teamPlans++;
                _teamLast=$"R{round} {side} {executed} bot purchases / {purchases.Count} planned";
            }
            catch (Exception ex)
            {
                _teamLast=ex.Message;
                Console.WriteLine($"[BotBuy] Team plan stopped: {ex.Message}");
                if (!pawns.Any(_processedPawns.Contains)) { _teamClaimed.Remove(side); QueuePurchases(); }
            }
        });
        return true;
    }

    private async Task RequestPlan(string request,Action<TeamReply?> complete)
    {
        TeamReply? reply=null;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var tcp=new TcpClient();
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback,_plannerPort,timeout.Token).ConfigureAwait(false);
            using var stream=tcp.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request+"\n"),timeout.Token).ConfigureAwait(false);
            using var reader=new StreamReader(stream);
            string? response=await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (response!=null) reply=JsonSerializer.Deserialize<TeamReply>(response,TeamJson);
        }
        catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or JsonException)
        { Console.WriteLine($"[BotBuy] Team planner unavailable: {ex.Message}"); }
        Server.NextFrame(()=>complete(reply));
    }
}
