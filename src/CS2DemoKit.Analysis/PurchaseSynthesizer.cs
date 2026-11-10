#region

using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Synthesizes <c>item_purchase</c> and <c>item_refund</c> after each frame from two per-player
///     columns, <c>entity.controller.cash_spent</c> and <c>entity.controller.money</c>, and the frame's
///     <c>item_pickup</c> events. Built only when a ruleset subscribes to one of the two views.
///     <para>
///         A rise of a slot's cash spent is a purchase costing the rise; the item is that slot's pickup on
///         the same frame. Measured over 31 tournament and 10 matchmaking demos: 98.4% and 98.7% of rises
///         carry a same-frame pickup, a window of one or two frames recovers almost none of the rest (an
///         item bought for a teammate, picked up later by them), and the account fell by exactly the rise
///         in all but 2 of 42,528 rises. A fall with the account rising by the same amount is a refund;
///         a fall that leaves the account alone is the round's reset.
///     </para>
///     <para>
///         The price list is the one current at the end of 2026 and is used only to split a frame with
///         several pickups; a cost is otherwise always the measured rise, so a build that changes a price
///         leaves single purchases exact and makes a multi-item frame fall back to one joined event.
///     </para>
/// </summary>
internal sealed class PurchaseSynthesizer
{
    internal const string CashSpentProvider = "entity.controller.cash_spent";
    internal const string MoneyProvider = "entity.controller.money";
    internal const string Unknown = "unknown";

    private const int MaxSlots = 64;

    private static readonly Dictionary<string, int> _prices = new(StringComparer.Ordinal)
    {
        ["glock"] = 200, ["hkp2000"] = 200, ["usp_silencer"] = 200, ["p250"] = 300, ["elite"] = 300,
        ["fiveseven"] = 500, ["tec9"] = 500, ["cz75a"] = 500, ["deagle"] = 700, ["revolver"] = 600,
        ["mac10"] = 1050, ["mp9"] = 1250, ["mp7"] = 1400, ["mp5sd"] = 1400, ["ump45"] = 1200, ["p90"] = 2350,
        ["bizon"] = 1300, ["galilar"] = 1800, ["famas"] = 1950, ["ak47"] = 2700, ["m4a1"] = 2900,
        ["m4a1_silencer"] = 2900, ["sg556"] = 3000, ["aug"] = 3300, ["ssg08"] = 1700, ["awp"] = 4750,
        ["g3sg1"] = 5000, ["scar20"] = 5000, ["nova"] = 1050, ["xm1014"] = 2000, ["sawedoff"] = 1100,
        ["mag7"] = 1300, ["m249"] = 5200, ["negev"] = 1700, ["vest"] = 650, ["vesthelm"] = 1000,
        ["defuser"] = 400, ["taser"] = 200, ["hegrenade"] = 300, ["flashbang"] = 200, ["smokegrenade"] = 300,
        ["molotov"] = 400, ["incgrenade"] = 500, ["decoy"] = 50
    };

    // The R8 comes through as `deagle`; its definition index says which.
    private const int RevolverDefIndex = 64;

    private readonly int _cashColumn;
    private readonly int _moneyColumn;
    private readonly int[] _lastSpent = new int[MaxSlots];
    private readonly int[] _lastMoney = new int[MaxSlots];
    private readonly List<(string Item, int DefIndex, int Cost)>?[] _bought = new List<(string, int, int)>?[MaxSlots];
    private readonly List<(string Item, int DefIndex)>?[] _pickups = new List<(string, int)>?[MaxSlots];
    private readonly List<NetMessage> _out = [];

    internal PurchaseSynthesizer(IReadOnlyList<string> columns)
    {
        _cashColumn = IndexOf(columns, CashSpentProvider);
        _moneyColumn = IndexOf(columns, MoneyProvider);
        Reset();
    }

    internal void Reset()
    {
        Array.Fill(_lastSpent, -1);
        Array.Fill(_lastMoney, -1);
        Array.Clear(_bought);
    }

    /// <summary>The purchases and refunds of the frame just folded, in slot order.</summary>
    internal IReadOnlyList<NetMessage> Step(IReadOnlyList<NetMessage> frameMessages, PreFrameSnapshot cells, int tick)
    {
        _out.Clear();
        foreach (List<(string, int)>? list in _pickups)
        {
            list?.Clear();
        }

        for (int m = 0; m < frameMessages.Count; m++)
        {
            if (frameMessages[m] is GameEventMessage { DecodedEvent.Payload: ItemPickupEvent pickup }
                && pickup.UserId is >= 0 and < MaxSlots && IsBuyable(pickup.Item))
            {
                (_pickups[pickup.UserId] ??= []).Add((pickup.Item, pickup.DefIndex));
            }
        }

        for (int slot = 0; slot < MaxSlots; slot++)
        {
            if (!cells.TryGetInt(_cashColumn, slot, out int spent))
            {
                continue;
            }

            int money = cells.TryGetInt(_moneyColumn, slot, out int m) ? m : -1;
            int lastSpent = _lastSpent[slot], lastMoney = _lastMoney[slot];
            _lastSpent[slot] = spent;
            _lastMoney[slot] = money;
            if (lastSpent < 0 || spent == lastSpent)
            {
                continue;
            }

            if (spent > lastSpent)
            {
                Purchase(slot, spent - lastSpent, tick);
            }
            else if (lastMoney >= 0 && money - lastMoney == lastSpent - spent)
            {
                Refund(slot, lastSpent - spent, tick);
            }
            else
            {
                // The round's reset.
                _bought[slot]?.Clear();
            }
        }

        return _out;
    }

    private void Purchase(int slot, int rise, int tick)
    {
        List<(string Item, int DefIndex)> items = _pickups[slot] is { Count: > 0 } p ? [.. p] : [];

        // One vesthelm buy fires a vest and a vesthelm pickup.
        if (items.Any(i => i.Item == "vesthelm"))
        {
            items.RemoveAll(i => i.Item == "vest");
        }

        if (items.Count == 0)
        {
            Emit(slot, Unknown, -1, rise, tick);
        }
        else if (items.Count == 1)
        {
            Emit(slot, items[0].Item, items[0].DefIndex, rise, tick);
        }
        else if (items.All(i => Price(i) is not null) && items.Sum(i => Price(i)!.Value) == rise)
        {
            foreach ((string item, int defIndex) in items)
            {
                Emit(slot, item, defIndex, Price((item, defIndex))!.Value, tick);
            }
        }
        else
        {
            Emit(slot, string.Join("+", items.Select(i => i.Item)), -1, rise, tick);
        }
    }

    private void Refund(int slot, int amount, int tick)
    {
        string item = Unknown;
        int defIndex = -1;
        if (_bought[slot] is { } bought && bought.FindLastIndex(b => b.Cost == amount) is var at and >= 0)
        {
            (item, defIndex, _) = bought[at];
            bought.RemoveAt(at);
        }

        _out.Add(GameEventMessage.ForSynthesizedEvent(new ItemRefundedEvent(tick, tick, tick, slot, item, defIndex, amount)));
    }

    private void Emit(int slot, string item, int defIndex, int cost, int tick)
    {
        (_bought[slot] ??= []).Add((item, defIndex, cost));
        _out.Add(GameEventMessage.ForSynthesizedEvent(new ItemPurchasedEvent(tick, tick, tick, slot, item, defIndex, cost)));
    }

    private static int? Price((string Item, int DefIndex) item) =>
        item.DefIndex == RevolverDefIndex ? 600 : _prices.TryGetValue(item.Item, out int price) ? price : null;

    private static bool IsBuyable(string? item) =>
        item is not null && item != "c4" && !item.Contains("knife", StringComparison.Ordinal)
        && !item.Contains("bayonet", StringComparison.Ordinal);

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"purchase synthesis needs the '{name}' column, which this run does not carry");
    }
}
