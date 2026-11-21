using CounterStrikeSharp.API;
using static CounterStrikeSharp.API.Modules.Memory.DataType;
using System.Reflection;
using System.Runtime.InteropServices;
using BotNative;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace BotBuyPatch;

internal sealed class BotBuyNative : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint LoadoutItem(nint inventoryServices, int team, int slot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ItemPrice(nint weaponServices, ushort definition, nint item);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CanBuy(nint pawn, byte displayMessage);

    internal readonly record struct Offer(int Slot, int Price, ushort Definition);
    private readonly BotBuyPatch owner;
    private readonly MemoryFunctionWithReturn<nint, int, byte, byte, int> purchase;
    private readonly LoadoutItem loadoutItem;
    private readonly ItemPrice itemPrice;
    private readonly CanBuy canBuy;
    private nint permittedService, captureItemService, droppedWeapon;
    private ushort captureDefinition;
    private nint sponsorView, recipientItems, sponsoredEntity;
    private string? sponsoredName;
    private bool sponsorHooked;
    private bool hooked, captureHooked;

    internal BotBuyNative(BotBuyPatch owner)
    {
        this.owner = owner;
        var (data, server) = NativeGameData.ServerFunctions("buyLoadoutItem", "buyPrice", "buyAllowed", "buyPurchase");
        loadoutItem = data.Bind<LoadoutItem>(server, "buyLoadoutItem");
        itemPrice = data.Bind<ItemPrice>(server, "buyPrice");
        canBuy = data.Bind<CanBuy>(server, "buyAllowed");
        string signature = data.Signature("buyPurchase");
        var cache = typeof(BaseMemoryFunction).GetField("_createdFunctions", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Dictionary<string, nint>
            ?? throw new InvalidOperationException("CSS signature cache unavailable");
        // CSS retains detours after Unhook; reload must reuse its cached function.
        if (cache.TryGetValue(NativeGameData.CssSignatureKey(Addresses.ServerPath, signature, DATA_TYPE_INT, DATA_TYPE_POINTER, DATA_TYPE_INT, DATA_TYPE_CHAR, DATA_TYPE_CHAR), out nint cached) && cached != 0
            && Marshal.ReadByte(data.Address(server, "buyPurchase")) == 0xE9)
            data.VerifyDetour(server, "buyPurchase");
        else
            data.Verify(server, "buyPurchase");
        purchase = new(signature);
        if (purchase.Handle == 0) throw new InvalidOperationException("Native purchase binding failed");
        try
        {
            purchase.Hook(OnPurchase, HookMode.Pre); hooked = true;
            VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveItem, HookMode.Post); captureHooked = true;
            VirtualFunctions.GiveNamedItemFunc.Hook(OnSponsoredGrant, HookMode.Pre); sponsorHooked = true;
        }
        catch { Dispose(); throw; }
    }

    internal bool IsBuyAllowed(CCSPlayerController player)
        => player.PlayerPawn.Value is { IsValid: true } pawn && canBuy(pawn.Handle, 0) != 0;

    internal bool IsPrimarySlot(CCSPlayerController player, int slot)
    {
        if ((uint)slot >= 58 || player.InventoryServices is not { } inventory) return false;
        nint pointer = loadoutItem(inventory.Handle, (int)player.Team, slot);
        if (pointer == 0) return false;
        return new CEconItemView(pointer).ItemDefinitionIndex is
            7 or 8 or 9 or 10 or 11 or 13 or 14 or 16 or 17 or 19 or 23 or 24 or 25 or 26
            or 27 or 28 or 29 or 33 or 34 or 35 or 38 or 39 or 40 or 60;
    }

    internal Offer? Quote(CCSPlayerController player, string itemName)
    {
        ushort definition = Definition(itemName);
        if (player.InventoryServices is not { } inventory || player.PlayerPawn.Value?.WeaponServices is not { } weapons)
            return null;
        // Use the bot's real loadout, including InventorySimulator's replacements.
        for (int slot = 0; slot < 58; slot++)
        {
            nint pointer = loadoutItem(inventory.Handle, (int)player.Team, slot);
            if (pointer == 0) continue;
            var item = new CEconItemView(pointer);
            if (!item.Initialized || item.ItemDefinitionIndex != definition) continue;
            int price = itemPrice(weapons.Handle, definition, pointer);
            return price >= 0 ? new Offer(slot, price, definition) : null;
        }
        return null;
    }

    internal bool Buy(CCSPlayerController player, Offer offer, bool drop, out CBasePlayerWeapon? weapon)
    {
        weapon = null;
        var pawn = player.PlayerPawn.Value;
        if (pawn?.BuyServices is not { } services || pawn.ItemServices is not { } items) return false;
        if (permittedService != 0) throw new InvalidOperationException("Reentrant BotBuy transaction");
        permittedService = services.Handle;
        captureItemService = drop ? items.Handle : 0;
        captureDefinition = offer.Definition;
        droppedWeapon = 0;
        try
        {
            // The engine owns eligibility, price, money, inventory and item_purchase.
            // Its buy-and-drop mode preserves the donor's equipped primary.
            int result = purchase.Invoke(services.Handle, offer.Slot, drop ? (byte)0 : (byte)1,
                drop ? (byte)1 : (byte)0, bypasshook: false);
            if (result != 0) return false;
            if (drop)
            {
                if (droppedWeapon == 0) throw new InvalidOperationException("Successful buy-and-drop returned no weapon");
                weapon = new CBasePlayerWeapon(droppedWeapon);
                if (!weapon.IsValid) throw new InvalidOperationException("Purchased drop is no longer valid");
            }
            return true;
        }
        finally { permittedService = captureItemService = droppedWeapon = 0; }
    }

    // The recipient's native buy handles pistol replacement and grenade stacking.
    // Supply the payer's equipped econ view to the synchronous grant. Settle cash on
    // the payer only after the recipient's native purchase succeeds.
    internal bool Sponsor(CCSPlayerController payer, CCSPlayerController recipient, string name, Offer paid)
    {
        if (payer.Team != recipient.Team || payer.EntityHandle.Raw == recipient.EntityHandle.Raw ||
            payer.InGameMoneyServices is not { } from || recipient.InGameMoneyServices is not { } to ||
            payer.InventoryServices is not { } inventory || recipient.PlayerPawn.Value?.ItemServices is not { } items ||
            from.Account < paid.Price || name.StartsWith("item_") || !IsBuyAllowed(payer) || !IsBuyAllowed(recipient) ||
            Quote(recipient,name) is not { } offer || paid.Price != offer.Price) return false;
        nint view=loadoutItem(inventory.Handle,(int)payer.Team,paid.Slot);
        if (view==0 || !new CEconItemView(view).Initialized || new CEconItemView(view).ItemDefinitionIndex!=paid.Definition) return false;
        int balance=to.Account, spent=to.CashSpentThisRound, total=to.TotalCashSpent;
        int fromBalance=from.Account;
        bool accepted=false;
        try
        {
            sponsorView=view; recipientItems=items.Handle; sponsoredName=name; sponsoredEntity=0;
            to.Account=Math.Max(balance,offer.Price);
            accepted=Buy(recipient,offer,false,out _);
            if (!accepted) return false;
            from.Account=fromBalance-paid.Price;
            from.CashSpentThisRound+=paid.Price;
            from.TotalCashSpent+=paid.Price;
            if (sponsoredEntity!=0)
            {
                var weapon=new CBasePlayerWeapon(sponsoredEntity);
                weapon.OriginalOwnerXuidLow=(uint)payer.SteamID;
                weapon.OriginalOwnerXuidHigh=(uint)(payer.SteamID>>32);
                Utilities.SetStateChanged(weapon,"CEconEntity","m_OriginalOwnerXuidLow");
                Utilities.SetStateChanged(weapon,"CEconEntity","m_OriginalOwnerXuidHigh");
            }
            else if (paid.Definition is not (>=43 and <=48))
                throw new InvalidOperationException("Native purchase accepted without the sponsored weapon grant");
            return true;
        }
        finally
        {
            sponsorView=recipientItems=sponsoredEntity=0; sponsoredName=null;
            to.Account=balance; to.CashSpentThisRound=spent; to.TotalCashSpent=total;
            Utilities.SetStateChanged(recipient,"CCSPlayerController","m_pInGameMoneyServices");
            if (accepted) Utilities.SetStateChanged(payer,"CCSPlayerController","m_pInGameMoneyServices");
        }
    }

    private HookResult OnSponsoredGrant(DynamicHook hook)
    {
        if (sponsorView==0 || hook.GetParam<nint>(0)!=recipientItems) return HookResult.Continue;
        string name=hook.GetParam<string>(1);
        if (name!=sponsoredName) return HookResult.Continue;
        sponsoredEntity=VirtualFunctions.GiveNamedItemFunc.Invoke(recipientItems,name,hook.GetParam<nint>(2),
            sponsorView,hook.GetParam<nint>(4),hook.GetParam<nint>(5),bypasshook:true);
        hook.SetReturn(sponsoredEntity);
        return HookResult.Handled;
    }

    private HookResult OnPurchase(DynamicHook hook)
    {
        nint service = hook.GetParam<nint>(0);
        var pawn = new CCSPlayer_BuyServices(service).Pawn.Value?.As<CCSPlayerPawn>();
        var player = pawn?.Controller.Value?.As<CCSPlayerController>();
        if (player is not { IsValid: true } || pawn is not { IsValid: true }
            || player.PlayerPawn.Raw != pawn.EntityHandle.Raw) return HookResult.Continue;
        if (!owner.SuppressNativePurchase(player, service == permittedService, hook.GetParam<int>(1))) return HookResult.Continue;
        hook.SetReturn(3); // Native "not allowed" result, never pretend a blocked purchase succeeded.
        return HookResult.Handled;
    }

    private HookResult OnGiveItem(DynamicHook hook)
    {
        if (captureItemService != 0 && droppedWeapon == 0 && hook.GetParam<nint>(0) == captureItemService)
        {
            nint result = hook.GetReturn<nint>();
            if (result != 0 && new CBasePlayerWeapon(result).AttributeManager.Item.ItemDefinitionIndex == captureDefinition)
                droppedWeapon = result;
        }
        // Replay grants do not emit item_purchase. They are still real item
        // creation events and can make a donor ready to help a human teammate.
        if (permittedService == 0 && hook.GetReturn<nint>() is var created && created != 0)
        {
            var entity = new CBaseEntity(created);
            // GiveNamedItem also returns armor items, which have no weapon VData.
            if (!entity.DesignerName.StartsWith("weapon_", StringComparison.Ordinal)) return HookResult.Continue;
            if (entity.As<CCSWeaponBase>().VData?.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE)
            {
                var services = new CCSPlayer_ItemServices(hook.GetParam<nint>(0));
                if (services.Pawn.Value?.Controller.Value?.As<CCSPlayerController>() is { IsValid: true } player && BotBuyPatch.HasBotIdentity(player))
                    owner.OnReplayEquipmentCreated(player);
            }
        }
        return HookResult.Continue;
    }

    public void Dispose()
    {
        if (sponsorHooked) { VirtualFunctions.GiveNamedItemFunc.Unhook(OnSponsoredGrant,HookMode.Pre); sponsorHooked=false; }
        try { if (hooked) { purchase.Unhook(OnPurchase, HookMode.Pre); hooked = false; } }
        finally
        {
            if (captureHooked) { VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveItem, HookMode.Post); captureHooked = false; }
        }
    }

    internal static ushort Definition(string name) => name switch
    {
        "weapon_deagle" => 1, "weapon_elite" => 2, "weapon_fiveseven" => 3, "weapon_glock" => 4,
        "weapon_ak47" => 7, "weapon_aug" => 8, "weapon_awp" => 9, "weapon_famas" => 10,
        "weapon_g3sg1" => 11, "weapon_galilar" => 13, "weapon_m249" => 14, "weapon_m4a1" => 16,
        "weapon_mac10" => 17, "weapon_p90" => 19, "weapon_mp5sd" => 23, "weapon_ump45" => 24,
        "weapon_xm1014" => 25, "weapon_bizon" => 26, "weapon_mag7" => 27, "weapon_negev" => 28,
        "weapon_sawedoff" => 29, "weapon_tec9" => 30, "weapon_taser" => 31, "weapon_hkp2000" => 32,
        "weapon_mp7" => 33, "weapon_mp9" => 34, "weapon_nova" => 35, "weapon_p250" => 36,
        "weapon_scar20" => 38, "weapon_sg556" => 39, "weapon_ssg08" => 40, "weapon_flashbang" => 43,
        "weapon_hegrenade" => 44, "weapon_smokegrenade" => 45, "weapon_molotov" => 46,
        "weapon_decoy" => 47, "weapon_incgrenade" => 48, "item_kevlar" => 50, "item_assaultsuit" => 51,
        "item_defuser" => 55, "weapon_m4a1_silencer" => 60, "weapon_usp_silencer" => 61,
        "weapon_cz75a" => 63, "weapon_revolver" => 64,
        _ => throw new InvalidDataException($"Unknown buy-policy item: {name}")
    };
}
