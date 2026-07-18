# SiPV.Shop

Store-agnostic shop layer sitting on top of Unity IAP v5: one API (`ShopManager<T>` /
`IShopManager`) to define shop items in config, purchase them (IAP, currency-conversion,
rewarded-ad, or free), restore prior purchases, and grant/track rewards, regardless of
which concrete implementation backs the store connection (`UnityShopManager` today).

Depends on `SiPV.Config` (`ConfigManager` supplies `ConfigShopItem`/`ConfigIAPSku` assets),
`SiPV.UserData` (`UserDataManager` persists purchase counts, transaction dedup, and
grants/spends items via `Inventory`), `SiPV.Ads` (`AdsManager` shows the rewarded ad for
`ShopItemType.Ads` items), `SiPV.Event` (`EventManager` broadcasts every purchase
action/result event), `SiPV.Debugging` (`CustomLog`), `SiPV.Utilities`
(`MonoSingleton<T>`), `SiPV.Pool`/`SiPV.UI` (referenced by `ConfigShopItem`'s UI settings
for pooled shop-item widgets), and `UniTask` for async init/purchase orchestration.
`UnityShopManager` additionally depends on Unity IAP (`com.unity.purchasing`).

---

## Quick start

```csharp
using SiPVLib.Shop;

// Initialize once (usually at boot). Idempotent — safe to call again.
var ok = await UnityShopManager.Instance.Initialize();

// Purchase an item.
UnityShopManager.Instance.PurchaseItem(
    itemId: "starter_pack",
    placement: "shop_screen",
    onSuccess: item => CustomLog.Log($"Purchased {item.Id}"),
    onFailed: error => CustomLog.LogWarning($"Purchase failed: {error}")
);

// Restore previous purchases (e.g. reinstall, new device).
UnityShopManager.Instance.RestorePurchases(
    placement: "settings_screen",
    onSuccess: () => CustomLog.Log("Restore complete"),
    onFailed: error => CustomLog.LogWarning($"Restore failed: {error}")
);
```

Both `PurchaseItem` and `RestorePurchases` are fire-and-forget from the caller's
perspective — the callback and the broadcast events below both fire once the flow
settles, whichever your UI prefers to listen to.

---

## Shop item types

`ConfigShopItem.Type` (`ShopItemType`) selects the purchase flow:

| Type | Flow |
|---|---|
| `IAP` | Real-money purchase via the underlying store (`TryBeginIapPurchase`), confirmed through store callbacks. |
| `Conversion` | Spends `CostItems` from `Inventory`, grants `Items` — no store involved. |
| `Ads` | Shows a rewarded ad via `AdsManager.ShowRewardedAd`; grants `Items` only once the ad reports a reward (ad closed early or failed to display = purchase failed, no partial grant). |
| `Free` | Grants `DailyFreeItems`/`Items` directly, gated by `DailyFree` claims-per-day. |

All types share the same validation (`availability window`, `max amount reached`,
daily-free limits, IAP SKU presence, sufficient inventory for conversions) and the same
result broadcast.

---

## Events

Every purchase/restore attempt broadcasts through `SiPVLib.Event.EventManager`, both
globally and "targeted" to the specific item (or `ShopManager<T>.TargetRestorePurchases`
for restore actions):

| Event key | Payload | Fired when |
|---|---|---|
| `ShopManager<T>.EventShopAction` | `ShopActionEvent` | A purchase or restore is *started*. |
| `ShopManager<T>.EventShopResult` | `ShopResultEvent` | A purchase or restore *finishes* (success or failure). |

```csharp
using SiPVLib.Event;
using SiPVLib.Shop;

// Global — every shop result.
EventManager.Add<ShopResultEvent>(ShopManager<UnityShopManager>.EventShopResult, OnShopResult);
void OnShopResult(ShopResultEvent e)
{
    if (e.success) CustomLog.Log($"Granted {e.itemId} (count: {e.purchaseCount})");
}

// Targeted — only this item's results, with automatic cleanup on OnDestroy.
this.ListenEvent<ShopResultEvent>(
    ShopManager<UnityShopManager>.EventShopResult, OnStarterPackResult /* MonoBehaviour extension overload keyed by item id internally via GetTargetId */);
```

`ShopResultEvent` includes `itemId`, `actionType` (`Purchase`/`RestorePurchases`),
`placement`, `success`, `errorMessage`, `transactionId`, `grantedRewards`,
`alreadyProcessed` (true if a duplicate IAP callback was deduped, not re-granted), and
`purchaseCount`.

---

## Known limitations

- Restore-purchases completion is decoupled from individual item grants: the aggregate
  `RestoreTransactions` callback reports overall success/failure, while each restored item
  still flows through the normal purchase-succeeded path independently. A restore can
  report success even if an individual item's grant failed validation — check
  `EventShopResult` for the specific item if you need per-item restore confirmation.
- `UnityShopManager.OnStoreDisconnected` only logs a warning; there is no automatic
  reconnect/retry.
