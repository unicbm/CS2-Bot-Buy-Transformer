using System.Runtime.InteropServices;
using BotNative;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

public sealed partial class BotBuyPatch : BasePlugin
{
    public override string ModuleName => "CS2 Bot Buy Transformer";
    public override string ModuleVersion => "0.1.0-preview.2";
    public override string ModuleAuthor => "unicbm; based on ed0ard";
    public override string ModuleDescription => "Professional team purchases and native sponsorship. Source: https://github.com/unicbm/CS2-Bot-Buy-Transformer";

    private static readonly HashSet<string> PrimaryWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_ak47", "weapon_aug", "weapon_awp", "weapon_famas", "weapon_g3sg1", "weapon_galilar",
        "weapon_m249", "weapon_m4a1", "weapon_mac10", "weapon_p90", "weapon_mp5sd", "weapon_ump45",
        "weapon_xm1014", "weapon_bizon", "weapon_mag7", "weapon_negev", "weapon_sawedoff", "weapon_mp7",
        "weapon_mp9", "weapon_nova", "weapon_scar20", "weapon_sg556", "weapon_ssg08", "weapon_m4a1_silencer"
    };

    private static readonly HashSet<string> UtilityWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_flashbang", "weapon_hegrenade", "weapon_smokegrenade",
        "weapon_molotov", "weapon_incgrenade", "weapon_decoy"
    };

    private readonly HashSet<uint> _processedPawns = new();
    private readonly HashSet<uint> _nativeEquipmentPawns = new();
    private readonly HashSet<uint> _giftedPawns = new();
    private BuyPolicyDocument _policy = null!;
    private ProPrimaryPolicy? _proPrimaries;
    private readonly Dictionary<uint, (string Side, double[] Features)> _proInputs = new();
    private ProTransformer? _transformer;
    private readonly Dictionary<(uint Controller, string Side), List<PreviousBuy>> _history = new();
    private readonly Dictionary<uint, int> _freezeActions = new();
    private int _matchRound, _transformerDecisions;
    private string _lastTransformerDecision = "none yet";
    private BotBuyNative? _native;
    private int _round;
    private bool _roundOpen, _queued, _faulted;

    public override void Load(bool hotReload)
    {
        _policy = BuyPolicyDocument.Load(Path.Combine(ModuleDirectory, "buy-policy.json"));
        ConfigureLocalPlanner();
        NativeGameData.Configure(ModulePath);
        _native = new BotBuyNative(this);
        Console.WriteLine($"[BotBuy] native purchase hook active; policy demos={_policy.Training.DemoCount}");
        // CSS queues startup event hooks before the game loop exists, but its
        // unload path cannot remove those pending hooks. Register only after
        // native validation succeeds so a rejected build leaves no callbacks.
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventEnterBuyzone>(OnEnterBuyzone);
        RegisterEventHandler<EventItemPurchase>(OnItemPurchase);
        RegisterEventHandler<EventRoundFreezeEnd>((_, _) =>
        {
            if (_transformer != null) FinishTransformerFreeze();
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            ResetRound(false);
            _history.Clear();
            _matchRound = 0;
        });
        if (hotReload)
        {
            // Do not spend again on bots that already bought before a reload.
            foreach (var player in PlayerControllers().Where(IsUsablePlayer))
                _processedPawns.Add(player.PlayerPawn.Raw);
            _roundOpen = true;
        }
    }

    public override void Unload(bool hotReload)
    {
        ResetRound(false);
        _planner = null;
        _native?.Dispose();
        _native = null;
    }

    [ConsoleCommand("css_bot_pro_buy", "Show or set the professional primary-buy policy (0=off, 1=tree, 2=Transformer)")]
    public void SetProBuy(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount == 2 && command.GetArg(1) is "1" or "2")
        {
            try
            {
                if (command.GetArg(1) == "2" && _transformer == null)
                {
                    _transformer = ProTransformer.Load(Path.Combine(ModuleDirectory, "pro-transformer.json"),
                        PrimaryWeapons.Select(item => item["weapon_".Length..]));
                    _proPrimaries = null;
                    ResetProTracking();
                }
                else if (command.GetArg(1) == "1" && _proPrimaries == null)
                {
                    _proPrimaries = ProPrimaryPolicy.Load(Path.Combine(ModuleDirectory, "pro-primary-policy.json"));
                    _transformer = null;
                    ResetProTracking();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException)
            {
                command.ReplyToCommand($"[BotBuy] Could not enable professional purchases: {ex.Message}");
                return;
            }
        }
        else if (command.ArgCount == 2 && command.GetArg(1) == "0")
        {
            _proPrimaries = null;
            _transformer = null;
            ResetProTracking();
        }
        else if (command.ArgCount != 1)
        {
            command.ReplyToCommand("Usage: css_bot_pro_buy [0|1|2]");
            return;
        }
        command.ReplyToCommand(_transformer != null
            ? $"[BotBuy] Transformer on. Decisions: {_transformerDecisions}. Last: {_lastTransformerDecision}."
            : _proPrimaries != null ? "[BotBuy] Professional tree on." : "[BotBuy] Professional purchases off.");
    }

    private void ResetProTracking()
    {
        _history.Clear();
        _proInputs.Clear();
        _freezeActions.Clear();
        _transformerDecisions = 0;
        _lastTransformerDecision = "none yet";
    }

    private void ResetRound(bool open)
    {
        _round++;
        _roundOpen = open;
        _queued = false;
        _processedPawns.Clear();
        _nativeEquipmentPawns.Clear();
        _teamClaimed.Clear();
        _teamTried.Clear();
        _giftedPawns.Clear();
        _freezeActions.Clear();
        _proInputs.Clear();
    }

    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetRound(true);
        if ((_proPrimaries != null || _transformer != null) && PolicyEnabled()) CaptureProBuyInputs();
        QueuePurchases();
        return HookResult.Continue;
    }

    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        ResetRound(false);
        return HookResult.Continue;
    }

    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true }) QueuePurchases();
        return HookResult.Continue;
    }

    public HookResult OnEnterBuyzone(EventEnterBuyzone @event, GameEventInfo info)
    {
        if (@event.Canbuy) QueuePurchases();
        return HookResult.Continue;
    }

    public HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } purchaser && !HasBotIdentity(purchaser)) QueuePurchases();
        else if (_transformer != null && @event.Userid is { IsValid: true } buyer &&
            _proInputs.ContainsKey(buyer.PlayerPawn.Raw))
        {
            string item = @event.Weapon;
            if (item.StartsWith("weapon_", StringComparison.Ordinal)) item = item[7..];
            int action = Array.IndexOf(_transformer.Actions, item);
            if (action > 0)
            {
                uint pawn = buyer.PlayerPawn.Raw;
                // Multiple primary transactions (e.g. buying a second rifle for a teammate)
                // have no single training label.
                _freezeActions[pawn] = _freezeActions.ContainsKey(pawn) ? -1 : action;
            }
        }
        return HookResult.Continue;
    }

    // DTR grants gear after round_start; wait for that actual creation boundary
    // instead of assuming its donors were ready when our round callback ran.
    internal void OnReplayEquipmentCreated(CCSPlayerController player)
    {
        if (IsDtrOwned(player)) QueuePurchases();
    }

    private void QueuePurchases()
    {
        if (!_roundOpen || _queued || _faulted || _native == null) return;
        _queued = true;
        int round = _round;
        // One lifecycle callback after spawn/round initialization, not a timer
        // guessing when the native bot has finished buying.
        Server.NextFrame(() =>
        {
            if (round != _round) return;
            _queued = false;
            if (!_roundOpen || _faulted || _native == null || !PolicyEnabled()) return;
            try
            {
                var players = PlayerControllers().Where(player => IsUsablePlayer(player)
                    && player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist).ToList();
                ApplyEvidencePolicy(players.Where(player => !IsDtrOwned(player)).ToList());
                RunDropAssist(players);
            }
            catch (Exception ex)
            {
                _faulted = true;
                Console.WriteLine($"[BotBuy] purchase processing stopped; reload after fixing the error: {ex}");
            }
        });
    }

    private static bool PolicyEnabled()
        => !Server.MapName.Equals("aim_rush", StringComparison.OrdinalIgnoreCase)
            && !HasForcedBotLoadout()
            && !(Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules?.WarmupPeriod ?? true);

    internal bool SuppressNativePurchase(CCSPlayerController player, bool permitted, int slot)
    {
        if (!IsAutonomousBot(player)) return false;
        // An explicit teammate donation is separate from DTR's own loadout.
        if (permitted) return false;
        // Replay owns its equipment, including an explicit empty (skip) plan.
        if (IsDtrOwned(player)) return true;
        if (!PolicyEnabled()) return false;
        // Low-sample economy classes retain their existing native armor, pistol
        // and utility decisions. Only their primary selection belongs to us.
        if (_nativeEquipmentPawns.Contains(player.PlayerPawn.Raw)
            && _native?.IsPrimarySlot(player, slot) == false) return false;
        // A late spawn or a bot entering a buy zone can make its first native
        // request after round_start. The request itself wakes its one-shot plan.
        if (!_processedPawns.Contains(player.PlayerPawn.Raw)) QueuePurchases();
        return true;
    }

    private void ApplyEvidencePolicy(List<CCSPlayerController> roundPlayers)
    {
        if (_native == null)
            return;

        foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
        {
            var teamPlayers = roundPlayers.Where(player => IsUsablePlayer(player) && player.Team == team).ToList();
            var bots = teamPlayers.Where(player => CanManageBot(player)
                && !_processedPawns.Contains(player.PlayerPawn.Raw) && _native.IsBuyAllowed(player)).ToList();
            if (_teamClaimed.Contains(team) || bots.Count == 0)
                continue;

            if (bots.Count == teamPlayers.Count(p=>HasBotIdentity(p)) && BeginTeamPurchase(team,teamPlayers)) continue;

            TeamEconomyClass economyClass = ClassifyEconomy(teamPlayers);
            string side = team == CsTeam.CounterTerrorist ? "ct" : "t";
            var policy = _policy.Find(side, economyClass);

            foreach (var bot in bots) _processedPawns.Add(bot.PlayerPawn.Raw);
            var primaryPolicy = _policy.Find(side, TeamEconomyClass.Full);
            if (economyClass != TeamEconomyClass.Pistol && primaryPolicy is { Eligible: true })
                BuyPrimaries(teamPlayers, bots, primaryPolicy);

            if (policy is not { Eligible: true })
            {
                foreach (var bot in bots) _nativeEquipmentPawns.Add(bot.PlayerPawn.Raw);
                continue;
            }

            ApplyArmorTargets(bots, policy);
            if (economyClass == TeamEconomyClass.Pistol)
                ApplyPistolSecondaryTargets(bots, policy);
            if (team == CsTeam.CounterTerrorist)
                ApplyDefuserTargets(bots, policy);
            ApplyUtilityTargets(bots, policy);
        }
    }

    private TeamEconomyClass ClassifyEconomy(List<CCSPlayerController> players)
    {
        // The old classifier ran after native spending. At the new pre-purchase
        // boundary, available cash must be included alongside carried equipment.
        double available = players.Sum(player => (long)InventoryValue(player)
            + (player.InGameMoneyServices?.Account ?? 0)) / (double)players.Count;
        return BotBuyEconomy.Classify(IsRegulationPistolRound(), available, _policy.EconomyThresholds);
    }

    private void CaptureProBuyInputs()
    {
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        if (rules == null) return;
        int round = rules.TotalRoundsPlayed + 1;
        if (round <= _matchRound || (round > 12) != (_matchRound > 12)) _history.Clear();
        _matchRound = round;
        if (ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() != 24 ||
            rules.TotalRoundsPlayed is <= 0 or 12 or >= 24) return;
        var teams = PlayerControllers().Where(player => IsUsablePlayer(player) &&
            player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist).GroupBy(player => player.Team);
        foreach (var team in teams)
        {
            var players = team.ToArray();
            if (players.Length != 5 || players.Any(p => p.InGameMoneyServices == null || IsDtrOwned(p))) continue;
            double cash = players.Average(p => p.InGameMoneyServices!.Account);
            int primaries = players.Count(HasPrimaryWeapon);
            foreach (var p in players.Where(p => CanManageBot(p) && !HasPrimaryWeapon(p)))
                _proInputs[p.PlayerPawn.Raw] = (team.Key == CsTeam.Terrorist ? "2" : "3",
                    [p.InGameMoneyServices!.Account, cash, primaries, HasArmor(p) ? 1 : 0,
                     rules.TotalRoundsPlayed is 11 or 23 ? 1 : 0]);
        }
    }

    private void FinishTransformerFreeze()
    {
        foreach (var bot in PlayerControllers().Where(CanManageBot))
        {
            uint pawn = bot.PlayerPawn.Raw;
            if (!_processedPawns.Contains(pawn) || !_proInputs.TryGetValue(pawn, out var ctx)) continue;
            int action = _freezeActions.GetValueOrDefault(pawn);
            if (action < 0) continue;
            var key = (bot.EntityHandle.Raw, ctx.Side);
            if (!_history.TryGetValue(key, out var prior)) _history[key] = prior = [];
            prior.Add(new PreviousBuy(_matchRound, ctx.Features, action));
            if (prior.Count > _transformer!.Window) prior.RemoveAt(0);
        }
        _proInputs.Clear();
        _freezeActions.Clear();
    }

    private void BuyPrimaries(List<CCSPlayerController> teamPlayers, List<CCSPlayerController> bots, EconomyPolicy policy)
    {
        int teamAwps = teamPlayers.Count(player => HasItem(player, "weapon_awp"));
        float ecoLimit = IsLastRoundOfHalf() ? 0 : ConVar.Find("bot_eco_limit")?.GetPrimitiveValue<float>() ?? 2800;
        foreach (var bot in bots.OrderBy(_ => Random.Shared.Next()))
        {
            // A carried or gifted primary belongs to the bot; never refund it.
            if (HasPrimaryWeapon(bot)) continue;
            int money = bot.InGameMoneyServices?.Account ?? 0;
            string? item;
            bool usedTransformer = false;
            if (_proInputs.TryGetValue(bot.PlayerPawn.Raw, out var ctx))
            {
                bool Available(string candidate) => (candidate != "weapon_awp" || teamAwps == 0) &&
                    _native?.Quote(bot, candidate) is { Price: >= 0 } quote && quote.Price <= money;
                if (_transformer != null)
                {
                    _history.TryGetValue((bot.EntityHandle.Raw, ctx.Side), out var prior);
                    var p = _transformer.Probabilities(ctx.Side, ctx.Features, bot.PlayerName, _matchRound, prior ?? []);
                    item = ProPrimaryPolicy.Sample(_transformer.Actions, p, Available);
                    usedTransformer = true;
                }
                else item = _proPrimaries!.Pick(ctx.Side, ctx.Features, bot.PlayerName, Available);
            }
            else
            {
                if (money < ecoLimit) continue;
                item = PickWeightedAffordable(bot, policy.PrimaryChoices,
                    choice => choice.Item != "weapon_awp" || teamAwps == 0);
            }
            bool bought = item != null && Buy(bot, item);
            if (bought && item == "weapon_awp") teamAwps++;
            if (usedTransformer)
            {
                _transformerDecisions++;
                string result = bought ? item! : item == null ? "no primary" : $"purchase rejected ({item})";
                _lastTransformerDecision = $"R{_matchRound} {bot.PlayerName} ${money} -> {result}";
            }
        }
    }

    private void ApplyArmorTargets(List<CCSPlayerController> bots, EconomyPolicy policy)
    {
        int armorTarget = TargetCount(policy.ArmorRate, bots.Count);
        int helmetTarget = Math.Min(armorTarget, TargetCount(policy.HelmetRate, bots.Count));
        int armorCount = bots.Count(HasArmor);
        int helmetCount = bots.Count(HasHelmet);

        foreach (var bot in bots.Where(player => !HasArmor(player)).OrderBy(_ => Random.Shared.Next()))
        {
            if (armorCount >= armorTarget)
                break;
            bool buyHelmet = helmetCount < helmetTarget;
            if (Buy(bot, buyHelmet ? "item_assaultsuit" : "item_kevlar"))
            {
                armorCount++;
                if (buyHelmet)
                    helmetCount++;
            }
        }

        foreach (var bot in bots.Where(player => HasArmor(player) && !HasHelmet(player)).OrderBy(_ => Random.Shared.Next()))
        {
            if (helmetCount >= helmetTarget)
                break;
            if (Buy(bot, "item_assaultsuit"))
                helmetCount++;
        }
    }

    private void ApplyPistolSecondaryTargets(List<CCSPlayerController> bots, EconomyPolicy policy)
    {
        if (policy.SecondaryUpgrades.Count == 0)
            return;

        var upgradeNames = policy.SecondaryUpgrades.Select(choice => choice.Item)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int target = TargetCount(policy.SecondaryUpgradeRate, bots.Count);
        int current = bots.Count(bot => Weapons(bot).Any(upgradeNames.Contains));

        foreach (var bot in bots.OrderBy(_ => Random.Shared.Next()))
        {
            if (current >= target)
                break;
            string? defaultPistol = bot.Team == CsTeam.CounterTerrorist
                ? Weapons(bot).FirstOrDefault(item => item is "weapon_hkp2000" or "weapon_usp_silencer")
                : Weapons(bot).FirstOrDefault(item => item == "weapon_glock");
            if (defaultPistol == null)
                continue;

            string? upgrade = PickWeightedAffordable(bot, policy.SecondaryUpgrades);
            if (upgrade == null)
                continue;

            if (Buy(bot, upgrade)) current++;
        }
    }

    private void ApplyDefuserTargets(List<CCSPlayerController> bots, EconomyPolicy policy)
    {
        int target = TargetCount(policy.DefuserRate, bots.Count);
        int current = bots.Count(HasDefuser);
        foreach (var bot in bots.Where(player => !HasDefuser(player)).OrderBy(_ => Random.Shared.Next()))
        {
            if (current >= target)
                break;
            if (Buy(bot, "item_defuser"))
                current++;
        }
    }

    private void ApplyUtilityTargets(List<CCSPlayerController> bots, EconomyPolicy policy)
    {
        foreach (var (item, perPlayer) in policy.UtilityPerPlayer
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => GetEquipmentValue(pair.Key)))
        {
            int targetOwners = TargetCount(Math.Min(perPlayer, 1.0), bots.Count);
            int currentOwners = bots.Count(bot => HasItem(bot, item));
            foreach (var bot in bots.Where(player => !HasItem(player, item)).OrderBy(_ => Random.Shared.Next()))
            {
                if (currentOwners >= targetOwners)
                    break;
                if (UtilityCount(bot) >= 4)
                    continue;
                if (Buy(bot, item))
                    currentOwners++;
            }
        }
    }

    private void RunDropAssist(List<CCSPlayerController> players)
    {
        if (_native == null || IsFirstRoundOfHalf()) return;
        var recipients = players.Where(player => !_teamClaimed.Contains(player.Team) && CanReceiveDrop(player) && !HasPrimaryWeapon(player))
            .OrderBy(player => player.InGameMoneyServices!.Account).ToList();
        foreach (var recipient in recipients)
        {
            if (!CanReceiveDrop(recipient) || HasPrimaryWeapon(recipient)
                || _giftedPawns.Contains(recipient.PlayerPawn.Raw)) continue;
            foreach (var donor in players.Where(player => player != recipient && player.Team == recipient.Team
                && IsAutonomousBot(player) && HasPrimaryWeapon(player)
                && (_processedPawns.Contains(player.PlayerPawn.Raw) || IsDtrOwned(player))
                && BotBuyEconomy.AllowsAssistance(IsDtrOwned(player), IsDtrOwned(recipient), IsHumanControlled(recipient)))
                .OrderByDescending(player => player.InGameMoneyServices?.Account ?? 0))
            {
                string[] rifles = donor.Team == CsTeam.CounterTerrorist
                    ? (Random.Shared.Next(2) == 0 ? ["weapon_m4a1_silencer", "weapon_m4a1"] : ["weapon_m4a1", "weapon_m4a1_silencer"])
                    : ["weapon_ak47"];
                var offers = rifles.Select(item => _native.Quote(donor, item)).Where(offer => offer.HasValue
                    && BotBuyEconomy.CanSponsor(donor.InGameMoneyServices?.Account ?? 0,
                        recipient.InGameMoneyServices!.Account, offer.Value.Price)).ToList();
                if (offers.Count == 0 || !_native.IsBuyAllowed(donor)) continue;
                var origin = recipient.PlayerPawn.Value?.AbsOrigin;
                if (origin == null) continue;
                if (!_native.Buy(donor, offers[0]!.Value, drop: true, out var weapon)) continue;
                _giftedPawns.Add(recipient.PlayerPawn.Raw);
                // Randomizer's GiveNamedItem hooks resolve the donor's evidence or
                // stable random skin. Move that same entity, never recreate it for
                // the receiver. Native bot purchases leave original-owner XUID zero.
                weapon!.OriginalOwnerXuidLow = (uint)donor.SteamID;
                weapon.OriginalOwnerXuidHigh = (uint)(donor.SteamID >> 32);
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
                weapon!.Teleport(new Vector(origin.X, origin.Y, origin.Z + 8), null, new Vector(0, 0, 0));
                foreach (var teammate in players.Where(player => player.IsValid && !HasBotIdentity(player) && player.Team == donor.Team))
                    teammate.PrintToChat($"{ChatColors.Green}{donor.PlayerName}{ChatColors.Yellow}: {recipient.PlayerName}, I dropped a weapon for ya");
                break;
            }
        }
    }

    private bool CanReceiveDrop(CCSPlayerController player)
        => IsUsablePlayer(player) && !player.IsHLTV && player.InGameMoneyServices != null
            && (!IsDtrOwned(player) || IsHumanControlled(player))
            && _native?.IsBuyAllowed(player) == true;

    private static bool IsHumanControlled(CCSPlayerController player)
        => !HasBotIdentity(player) || player.PlayerPawn.Value?.Controller.Value?.As<CCSPlayerController>() is { IsValid: true } controller && !HasBotIdentity(controller);

    private string? PickWeightedAffordable(
        CCSPlayerController player,
        IEnumerable<WeightedItem> choices,
        Func<WeightedItem, bool>? extraFilter = null)
    {
        int money = player.InGameMoneyServices?.Account ?? 0;
        var candidates = choices
            .Where(choice => choice.Count > 0 && _native?.Quote(player, choice.Item) is { Price: >= 0 } offer && offer.Price <= money)
            .Where(choice => extraFilter?.Invoke(choice) ?? true)
            .ToList();
        int total = candidates.Sum(choice => choice.Count);
        if (total <= 0)
            return null;

        int roll = Random.Shared.Next(total);
        foreach (var choice in candidates)
        {
            roll -= choice.Count;
            if (roll < 0)
                return choice.Item;
        }
        return candidates[^1].Item;
    }

    [ConsoleCommand("css_bot_buy_smoke", "Buy one smoke through the selected bot's native loadout")]
    [CommandHelper(minArgs: 1, usage: "<slot>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void BuySmoke(CCSPlayerController? caller, CommandInfo command)
    {
        if (!int.TryParse(command.GetArg(1), out int slot) || slot < 0 || slot >= Server.MaxPlayers ||
            Utilities.GetPlayerFromSlot(slot) is not { } player || !CanManageBot(player))
        { command.ReplyToCommand("No eligible bot at that slot."); return; }
        if (Weapons(player).Contains("weapon_smokegrenade"))
        { command.ReplyToCommand("Smoke already in inventory."); return; }
        bool bought = _native?.IsBuyAllowed(player) == true && Buy(player, "weapon_smokegrenade");
        command.ReplyToCommand(bought ? "Native smoke purchase accepted." : "Native smoke purchase unavailable.");
    }

    private bool Buy(CCSPlayerController player, string itemName)
    {
        if (!CanManageBot(player) || _native == null) return false;
        var offer = _native.Quote(player, itemName);
        return offer.HasValue && offer.Value.Price <= (player.InGameMoneyServices?.Account ?? 0)
            && _native.Buy(player, offer.Value, drop: false, out _);
    }

    private static IEnumerable<string> Weapons(CCSPlayerController player)
    {
        var weaponServices = player.PlayerPawn.Value?.WeaponServices;
        if (weaponServices == null)
            yield break;
        foreach (var handle in weaponServices.MyWeapons)
        {
            if (handle.Value is { IsValid: true } weapon && !string.IsNullOrWhiteSpace(weapon.DesignerName))
                yield return weapon.DesignerName;
        }
    }

    private static string? GetPrimaryWeapon(CCSPlayerController player)
        => Weapons(player).FirstOrDefault(PrimaryWeapons.Contains);

    private static bool HasPrimaryWeapon(CCSPlayerController player)
        => GetPrimaryWeapon(player) != null;

    private static bool HasItem(CCSPlayerController player, string itemName)
        => Weapons(player).Any(item => item.Equals(itemName, StringComparison.OrdinalIgnoreCase));

    private static int UtilityCount(CCSPlayerController player)
        => Weapons(player).Count(UtilityWeapons.Contains);

    private static bool HasArmor(CCSPlayerController player)
        => (player.PlayerPawn.Value?.ArmorValue ?? 0) > 0;

    private static bool HasHelmet(CCSPlayerController player)
        => TryGetItemServices(player)?.HasHelmet ?? false;

    private static bool HasDefuser(CCSPlayerController player)
        => TryGetItemServices(player)?.HasDefuser ?? false;

    private static CCSPlayer_ItemServices? TryGetItemServices(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
            return null;
        return new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
    }

    private static IEnumerable<CCSPlayerController> PlayerControllers()
        => Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").Where(p=>p.IsValid);

    // BotHider can clear the displayed bot flag while the pawn keeps its native CCSBot.
    internal static bool HasBotIdentity(CCSPlayerController player)
        => player.IsBot || player.PlayerPawn.Value is { IsValid: true, Bot.Handle: not 0 };

    private static bool IsUsablePlayer(CCSPlayerController player)
        => player.IsValid && player.PawnIsAlive && player.PlayerPawn.Value is { IsValid: true };

    private static bool CanManageBot(CCSPlayerController player)
        => IsAutonomousBot(player) && !IsDtrOwned(player);

    private static bool IsAutonomousBot(CCSPlayerController player)
        => IsUsablePlayer(player) && HasBotIdentity(player) && !player.IsHLTV && !player.ControllingBot
            && !player.HasBeenControlledByPlayerThisRound;

    private static bool IsDtrOwned(CCSPlayerController player)
        => DtrBuyEvidence.HasPlan(player.Slot);

    private static int InventoryValue(CCSPlayerController player)
    {
        int value = Weapons(player).Sum(GetEquipmentValue);
        if (HasArmor(player))
            value += 650;
        if (HasHelmet(player))
            value += 350;
        if (HasDefuser(player))
            value += 400;
        return value;
    }

    private static int GetEquipmentValue(string itemName)
    {
        // Mirrors the DemoTracer manifest classifier's side-independent weapon
        // values, so a saved enemy rifle still counts toward the team economy.
        return itemName switch
        {
            "weapon_deagle" => 700,
            "weapon_elite" or "weapon_p250" => 300,
            "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a" => 500,
            "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer" => 200,
            "weapon_revolver" => 600,
            "weapon_ak47" => 2700,
            "weapon_aug" => 3300,
            "weapon_awp" => 4750,
            "weapon_famas" => 2050,
            "weapon_g3sg1" or "weapon_scar20" => 5000,
            "weapon_galilar" => 1800,
            "weapon_m249" => 5200,
            "weapon_m4a1" => 3100,
            "weapon_mac10" => 1050,
            "weapon_p90" => 2350,
            "weapon_mp5sd" or "weapon_mp7" => 1500,
            "weapon_ump45" => 1200,
            "weapon_xm1014" => 2000,
            "weapon_bizon" => 1400,
            "weapon_mag7" => 1300,
            "weapon_negev" or "weapon_ssg08" => 1700,
            "weapon_sawedoff" => 1100,
            "weapon_mp9" => 1250,
            "weapon_nova" => 1050,
            "weapon_sg556" => 3000,
            "weapon_m4a1_silencer" => 2900,
            "weapon_taser" or "weapon_flashbang" => 200,
            "weapon_hegrenade" or "weapon_smokegrenade" => 300,
            "weapon_molotov" => 400,
            "weapon_decoy" => 50,
            "weapon_incgrenade" => 600,
            _ => 0
        };
    }

    private static int TargetCount(double rate, int players)
    {
        double exact = Math.Clamp(rate, 0.0, 1.0) * players;
        int whole = (int)Math.Floor(exact);
        return whole + (Random.Shared.NextDouble() < exact - whole ? 1 : 0);
    }

    private static bool HasForcedBotLoadout()
    {
        var botLoadout = ConVar.Find("bot_loadout");
        return botLoadout != null && !string.IsNullOrWhiteSpace(botLoadout.StringValue);
    }

    private static bool IsFirstRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int overtimeRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;
            maxRounds = maxRounds > 0 ? maxRounds : 24;
            overtimeRounds = overtimeRounds > 0 ? overtimeRounds : 6;
            int half = Math.Max(1, maxRounds / 2);
            int overtimeHalf = Math.Max(1, overtimeRounds / 2);
            return played == 0 || played == half || played == maxRounds ||
                (played > maxRounds && (played - maxRounds) % overtimeHalf == 0);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRegulationPistolRound()
    {
        try
        {
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            if (gameRules == null)
                return false;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            maxRounds = maxRounds > 0 ? maxRounds : 24;
            int played = gameRules.TotalRoundsPlayed;
            return played == 0 || played == Math.Max(1, maxRounds / 2);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLastRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            if (gameRules == null)
                return false;
            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            maxRounds = maxRounds > 0 ? maxRounds : 24;
            int half = Math.Max(1, maxRounds / 2);
            return played == half - 1 || played == maxRounds - 1;
        }
        catch
        {
            return false;
        }
    }

    private static class DtrBuyEvidence
    {
        private static bool _unavailable;

        [DllImport("dtr-controller", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DtrController_GetBuyPlanItemCount(int slot);

        public static bool HasPlan(int slot)
        {
            if (_unavailable || slot < 0)
                return false;
            try
            {
                // -1 means no owner; 0 is DTR's explicit skip; positive is an explicit plan.
                return BotBuyEconomy.OwnsReplayBuy(DtrController_GetBuyPlanItemCount(slot));
            }
            catch (Exception ex) when (
                ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _unavailable = true;
                return false;
            }
        }
    }
}
