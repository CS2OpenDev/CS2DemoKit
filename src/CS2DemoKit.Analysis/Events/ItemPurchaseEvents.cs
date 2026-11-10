#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized by <see cref="PurchaseSynthesizer" />: a purchase, read as a rise of the buyer's
///     <c>m_iCashSpentThisRound</c> on a frame. GOTV carries no <c>item_purchase</c>. The item is the
///     buyer's <c>item_pickup</c> on the same frame; <see cref="Cost" /> is always the exact rise when the
///     frame holds one purchase. All three clocks carry the frame tick, like every synthesized event.
///     <para>
///         <see cref="Item" /> is <c>unknown</c> (and <see cref="DefIndex" /> -1) when the buyer picked
///         nothing up on that frame: almost always an item bought straight for a teammate, about 1.5% of
///         purchases on the corpus measured. Several pickups on one frame are split by the current price
///         list when their prices add up to the rise; when they do not, one event carries the items joined
///         with <c>+</c> and the whole rise. Warmup purchases are reported too; gate on <c>match.live</c>.
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick of the purchase.</param>
/// <param name="ServerTick">The same tick.</param>
/// <param name="GameTick">The same tick.</param>
/// <param name="PlayerSlot">The buyer's slot.</param>
/// <param name="Item">
///     The <c>item_pickup</c> item name (<c>ak47</c>, <c>vesthelm</c>, ...). It collapses variants:
///     <c>m4a1</c> is the M4A4 or the M4A1-S and <c>deagle</c> the Deagle or the R8, so key on
///     <see cref="DefIndex" /> to tell them apart.
/// </param>
/// <param name="DefIndex">The item definition index from the pickup, or -1.</param>
/// <param name="Cost">What the purchase cost, in dollars.</param>
public sealed record ItemPurchasedEvent(
    int FrameNumber, int ServerTick, int GameTick, int PlayerSlot, string Item, int DefIndex, int Cost)
    : GameEvent("item_purchase", -1, FrameNumber, ServerTick, GameTick)
{
    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        [F("PlayerSlot", PlayerSlot), F("Item", Item), F("DefIndex", DefIndex), F("Cost", Cost)];
}

/// <summary>
///     Synthesized by <see cref="PurchaseSynthesizer" />: a sale back in buy time, read as a fall of the
///     player's <c>m_iCashSpentThisRound</c> with the account rising by the same amount on the same frame
///     (the fall to 0 at a round's start leaves the account alone, so it is not one). The item is the
///     player's latest purchase this round at that cost, else <c>unknown</c>.
/// </summary>
/// <param name="FrameNumber">The frame-clock tick of the refund.</param>
/// <param name="ServerTick">The same tick.</param>
/// <param name="GameTick">The same tick.</param>
/// <param name="PlayerSlot">The player refunded.</param>
/// <param name="Item">The refunded purchase's item, or <c>unknown</c>.</param>
/// <param name="DefIndex">The refunded purchase's definition index, or -1.</param>
/// <param name="Refund">The amount given back, positive.</param>
public sealed record ItemRefundedEvent(
    int FrameNumber, int ServerTick, int GameTick, int PlayerSlot, string Item, int DefIndex, int Refund)
    : GameEvent("item_refund", -1, FrameNumber, ServerTick, GameTick)
{
    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        [F("PlayerSlot", PlayerSlot), F("Item", Item), F("DefIndex", DefIndex), F("Refund", Refund)];
}
