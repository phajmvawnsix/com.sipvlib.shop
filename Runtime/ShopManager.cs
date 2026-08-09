using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
#if SIPV_ADS
using SiPVLib.Ads;
using SiPVLib.Ads.Parameters;
#endif
using SiPVLib.Config;
using SiPVLib.Config.Configs;
using SiPVLib.Debugging;
using SiPVLib.Event;
using SiPVLib.Shop.Configs;
using SiPVLib.UserData;
using SiPVLib.UserData.InventoryHelper;
using SiPVLib.Utilities;

namespace SiPVLib.Shop
{
    /// <summary>
    /// Base shop manager: loads <see cref="ConfigShopItem"/> configs, orchestrates purchase/restore
    /// flows (IAP, Conversion, Ads, Free), persists purchase state via <see cref="UserDataManager"/>,
    /// and broadcasts <see cref="EventShopAction"/>/<see cref="EventShopResult"/> via
    /// <see cref="EventManager"/>. Store-specific behavior (starting an IAP purchase, restoring
    /// purchases) is supplied by a concrete subclass, e.g. <see cref="UnityShopManager"/>.
    /// </summary>
    /// <typeparam name="T">The concrete shop manager type (CRTP), used by <see cref="MonoSingleton{T}"/>.</typeparam>
    public abstract class ShopManager<T> : MonoSingleton<T>, IShopManager where T : ShopManager<T>
    {
        public const string EventShopAction = "Shop.Action";
        public const string EventShopResult = "Shop.Result";
        public const string TargetRestorePurchases = "__restore__";

        private const string SourcePurchaseSpend = "shop.purchase.spend";
        private const string SourcePurchaseReward = "shop.purchase.reward";

        private readonly Dictionary<string, ConfigShopItem> _itemsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigIAPSku> _iapSkusByItemId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingPurchaseRequest> _pendingPurchases = new(StringComparer.Ordinal);

        private ConfigShopItem[] _allItems = Array.Empty<ConfigShopItem>();
        private RestorePurchasesRequest _pendingRestoreRequest;

        /// <summary>True once <see cref="Initialize"/> has completed successfully.</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Loads all <see cref="ConfigShopItem"/> configs (deduped, sorted by priority), resolves their
        /// IAP SKU configs, and calls <see cref="OnInitializeAsync"/> for store-specific setup.
        /// Idempotent: returns immediately if already initialized.
        /// </summary>
        public virtual async UniTask<bool> Initialize()
        {
            if (IsInitialized) return true;

            if (ConfigManager.Instance == null)
            {
                CustomLog.LogError("[ShopManager] ConfigManager instance was not found.");
                return false;
            }

            if (UserDataManager.Instance == null)
            {
                CustomLog.LogError("[ShopManager] UserDataManager instance was not found.");
                return false;
            }

            if (!UserDataManager.Instance.IsInitialized)
            {
                var userDataInitialized = await UserDataManager.Instance.Init();
                if (!userDataInitialized)
                {
                    CustomLog.LogError("[ShopManager] Failed to initialize UserDataManager.");
                    return false;
                }
            }

            var configs = ConfigManager.GetAll<ConfigShopItem>();
            Array.Sort(configs, ComparePriority);

            _itemsById.Clear();
            _iapSkusByItemId.Clear();

            var distinctItems = new List<ConfigShopItem>(configs.Length);
            foreach (var item in configs)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id)) continue;

                if (_itemsById.ContainsKey(item.Id))
                {
                    CustomLog.LogWarning($"[ShopManager] Duplicate shop item id '{item.Id}' detected. Keeping the first config.");
                    continue;
                }

                _itemsById[item.Id] = item;
                distinctItems.Add(item);

                if (!item.IsIap || string.IsNullOrWhiteSpace(item.SkuId)) continue;

                var skuConfig = ConfigManager.Get<ConfigIAPSku>(item.SkuId, ConfigLocation.Local, true);
                if (skuConfig == null)
                {
                    CustomLog.LogWarning($"[ShopManager] IAP SKU config '{item.SkuId}' was not found for item '{item.Id}'.");
                    continue;
                }

                _iapSkusByItemId[item.Id] = skuConfig;
            }

            _allItems = distinctItems.ToArray();
            var initialized = await OnInitializeAsync();
            IsInitialized = initialized;

            if (!initialized)
                CustomLog.LogError("[ShopManager] Initialization failed.");

            return initialized;
        }

        /// <summary>Looks up a shop item config by id.</summary>
        /// <returns>The matching <see cref="ConfigShopItem"/>, or null if not found.</returns>
        public ConfigShopItem GetItem(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return null;
            return _itemsById.TryGetValue(itemId, out var item) ? item : null;
        }

        /// <summary>Returns all loaded shop item configs, sorted by priority.</summary>
        public ConfigShopItem[] GetAllItems() => _allItems;

        /// <summary>Gets the IAP price for an item, preferring live store data (see <see cref="TryGetLiveIapInfo"/>) over the configured fallback price.</summary>
        public virtual decimal GetItemIAPPrice(string itemId)
        {
            if (TryGetLiveIapInfo(itemId, out var price, out _))
                return price;

            return TryGetIapSku(itemId, out var skuConfig)
                ? skuConfig.GetPrice(UnityEngine.Application.platform)
                : 0m;
        }

        /// <summary>Gets the IAP price for an item only if it matches the given currency, otherwise 0.</summary>
        public virtual decimal GetItemIAPPrice(string itemId, string currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
                return GetItemIAPPrice(itemId);

            var itemCurrency = GetItemIAPCurrency(itemId);
            return string.Equals(itemCurrency, currency, StringComparison.OrdinalIgnoreCase)
                ? GetItemIAPPrice(itemId)
                : 0m;
        }

        /// <summary>Gets the ISO currency code for an item's live IAP price, or empty if unavailable.</summary>
        public virtual string GetItemIAPCurrency(string itemId)
        {
            return TryGetLiveIapInfo(itemId, out _, out var currency)
                ? currency
                : string.Empty;
        }

        /// <summary>Gets the number of times an item has been successfully purchased by the current user.</summary>
        public long GetItemPurchaseCount(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId) || UserDataManager.Instance == null)
                return 0;

            return UserDataManager.Instance.Get<long>(ShopKeys.PurchaseCount(itemId));
        }

        /// <summary>
        /// Starts a purchase for the given item id (IAP, Conversion, Ads, or Free depending on item type).
        /// Fire-and-forget; result is delivered via <paramref name="onSuccess"/>/<paramref name="onFailed"/>
        /// and broadcast via <see cref="EventShopResult"/> (both globally and per-item targeted).
        /// </summary>
        /// <param name="itemId">Id of the <see cref="ConfigShopItem"/> to purchase.</param>
        /// <param name="placement">Caller-defined UI placement/context, included in broadcast events.</param>
        public void PurchaseItem(string itemId, string placement, Action<ConfigShopItem> onSuccess = null,
            Action<string> onFailed = null)
        {
            PurchaseItemInternalAsync(itemId, placement, onSuccess, onFailed).Forget();
        }

        /// <summary>
        /// Restores previously completed purchases (e.g. after reinstall) via <see cref="TryBeginRestorePurchases"/>.
        /// Fire-and-forget; only one restore request may be in flight at a time. Result is delivered via
        /// <paramref name="onSuccess"/>/<paramref name="onFailed"/> and broadcast via <see cref="EventShopResult"/>
        /// with <see cref="ShopActionType.RestorePurchases"/>.
        /// </summary>
        /// <param name="placement">Caller-defined UI placement/context, included in broadcast events.</param>
        public void RestorePurchases(string placement, Action onSuccess = null, Action<string> onFailed = null)
        {
            RestorePurchasesInternalAsync(placement, onSuccess, onFailed).Forget();
        }

        /// <summary>Override hook for subclass store-specific initialization (e.g. connecting to Unity IAP). Default is a no-op success.</summary>
        protected virtual UniTask<bool> OnInitializeAsync() => UniTask.FromResult(true);

        /// <summary>Override hook to supply a live store price/currency for an item. Default reports unavailable.</summary>
        protected virtual bool TryGetLiveIapInfo(string itemId, out decimal price, out string currency)
        {
            price = 0m;
            currency = string.Empty;
            return false;
        }

        /// <summary>Override hook: start the actual store purchase for an IAP item. Must call <see cref="HandleIapPurchaseSucceeded"/>/<see cref="HandleIapPurchaseFailed"/> once the store responds.</summary>
        protected abstract bool TryBeginIapPurchase(ConfigShopItem item, PendingPurchaseRequest request, out string error);

        /// <summary>Override hook: start restoring purchases via the underlying store. Must call <see cref="CompleteRestorePurchases"/> once the store responds. Default reports unsupported.</summary>
        protected virtual bool TryBeginRestorePurchases(RestorePurchasesRequest request, out string error)
        {
            error = "Restore purchases is not supported by this shop manager.";
            return false;
        }

        protected bool HasPendingPurchase(string itemId) => !string.IsNullOrWhiteSpace(itemId) && _pendingPurchases.ContainsKey(itemId);

        protected bool TryGetIapSku(string itemId, out ConfigIAPSku skuConfig)
        {
            return _iapSkusByItemId.TryGetValue(itemId, out skuConfig);
        }

        /// <summary>
        /// Entry point for a subclass to report a successful IAP purchase (fresh or restored). Dedups by
        /// <paramref name="transactionId"/> via <see cref="UserDataManager"/> to avoid double-granting.
        /// </summary>
        /// <remarks>
        /// Not thread-safe: assumes it is called on the Unity main thread. Unity IAP v5's
        /// <c>StoreController</c> callbacks are dispatched on the main thread, which is why
        /// <c>_pendingPurchases</c> and related state here are accessed without locks. If a store
        /// backend cannot guarantee main-thread callbacks, the caller must marshal onto the main
        /// thread before invoking this method.
        /// </remarks>
        protected void HandleIapPurchaseSucceeded(string itemId, string transactionId, bool isRestorePurchase)
        {
            if (!_itemsById.TryGetValue(itemId, out var item))
            {
                CustomLog.LogError($"[ShopManager] Received purchase success for unknown item '{itemId}'.");
                return;
            }

            _pendingPurchases.TryGetValue(itemId, out var pendingRequest);
            _pendingPurchases.Remove(itemId);

            var actionType = isRestorePurchase ? ShopActionType.RestorePurchases : ShopActionType.Purchase;
            var placement = pendingRequest?.placement ?? string.Empty;
            var grantedRewards = false;
            var alreadyProcessed = false;

            if (!string.IsNullOrWhiteSpace(transactionId) && IsTransactionProcessed(transactionId))
            {
                alreadyProcessed = true;
            }
            else
            {
                var validationError = ValidateCommonPurchaseRules(item, isIapPurchase: true);
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    DispatchResult(CreateResultEvent(item, actionType, placement, false, validationError,
                        transactionId, false, false));
                    pendingRequest?.onFailed?.Invoke(validationError);
                    return;
                }

                grantedRewards = GrantItems(item.GetRewardItems());
                if (!grantedRewards)
                {
                    var rewardError = $"Failed to grant rewards for item '{itemId}'.";
                    DispatchResult(CreateResultEvent(item, actionType, placement, false, rewardError,
                        transactionId, false, false));
                    pendingRequest?.onFailed?.Invoke(rewardError);
                    return;
                }

                MarkTransactionProcessed(transactionId, itemId);
                IncreasePurchaseCounters(item);
            }

            var resultEvent = CreateResultEvent(item, actionType, placement, true, string.Empty,
                transactionId, grantedRewards, alreadyProcessed);
            DispatchResult(resultEvent);
            pendingRequest?.onSuccess?.Invoke(item);
        }

        /// <summary>Entry point for a subclass to report a failed IAP purchase.</summary>
        /// <remarks>Not thread-safe: assumes it is called on the Unity main thread (see <see cref="HandleIapPurchaseSucceeded"/>).</remarks>
        protected void HandleIapPurchaseFailed(string itemId, string error)
        {
            _pendingPurchases.TryGetValue(itemId, out var pendingRequest);
            _pendingPurchases.Remove(itemId);

            var item = GetItem(itemId);
            var resultEvent = CreateResultEvent(item, ShopActionType.Purchase,
                pendingRequest?.placement ?? string.Empty,
                false,
                string.IsNullOrWhiteSpace(error) ? "Purchase failed." : error,
                string.Empty,
                false,
                false);

            DispatchResult(resultEvent);
            pendingRequest?.onFailed?.Invoke(resultEvent.errorMessage);
        }

        /// <summary>Entry point for a subclass to report that the aggregate restore-purchases operation finished.</summary>
        /// <remarks>Not thread-safe: assumes it is called on the Unity main thread (see <see cref="HandleIapPurchaseSucceeded"/>).</remarks>
        protected void CompleteRestorePurchases(bool success, string error = null)
        {
            var request = _pendingRestoreRequest;
            _pendingRestoreRequest = null;
            if (request == null) return;

            var result = CreateResultEvent(null, ShopActionType.RestorePurchases, request.placement,
                success,
                success ? string.Empty : string.IsNullOrWhiteSpace(error) ? "Restore purchases failed." : error,
                string.Empty,
                false,
                false);

            DispatchResult(result);

            if (success) request.onSuccess?.Invoke();
            else request.onFailed?.Invoke(result.errorMessage);
        }

        private async UniTaskVoid PurchaseItemInternalAsync(string itemId, string placement,
            Action<ConfigShopItem> onSuccess, Action<string> onFailed)
        {
            var actionEvent = CreateActionEvent(itemId, ShopActionType.Purchase, placement, false);
            DispatchAction(actionEvent);

            if (!await EnsureInitializedAsync())
            {
                FailPurchase(GetItem(itemId), placement, onFailed, "Shop manager is not initialized.");
                return;
            }

            var item = GetItem(itemId);
            if (item == null)
            {
                FailPurchase(null, placement, onFailed, $"Shop item '{itemId}' was not found.");
                return;
            }

            var validationError = ValidateCommonPurchaseRules(item, item.IsIap);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                FailPurchase(item, placement, onFailed, validationError);
                return;
            }

            if (item.IsIap)
            {
                if (_pendingPurchases.ContainsKey(item.Id))
                {
                    FailPurchase(item, placement, onFailed,
                        $"Purchase for item '{item.Id}' is already in progress.");
                    return;
                }

                var request = new PendingPurchaseRequest(item.Id, placement, onSuccess, onFailed);
                _pendingPurchases[item.Id] = request;

                if (!TryBeginIapPurchase(item, request, out var error))
                {
                    _pendingPurchases.Remove(item.Id);
                    FailPurchase(item, placement, onFailed, error);
                }

                return;
            }

            switch (item.Type)
            {
                case ShopItemType.Conversion:
                    if (!TrySpendCostItems(item.CostItems, out var spendError))
                    {
                        FailPurchase(item, placement, onFailed, spendError);
                        return;
                    }

                    if (!GrantItems(item.GetRewardItems()))
                    {
                        FailPurchase(item, placement, onFailed, $"Failed to grant rewards for item '{item.Id}'.");
                        return;
                    }
                    break;

                case ShopItemType.Free:
                    if (!GrantItems(item.GetRewardItems()))
                    {
                        FailPurchase(item, placement, onFailed, $"Failed to grant rewards for item '{item.Id}'.");
                        return;
                    }
                    break;

                case ShopItemType.Ads:
#if SIPV_ADS
                    BeginAdsPurchase(item, placement, onSuccess, onFailed);
#else
                    FailPurchase(item, placement, onFailed,
                        $"Shop item '{item.Id}' requires the com.sipvlib.ads package, which is not installed.");
#endif
                    return;

                default:
                    FailPurchase(item, placement, onFailed,
                        $"Shop item '{item.Id}' has unsupported non-IAP type '{item.Type}'.");
                    return;
            }

            IncreasePurchaseCounters(item);
            DispatchResult(CreateResultEvent(item, ShopActionType.Purchase, placement, true,
                string.Empty, string.Empty, true, false));
            onSuccess?.Invoke(item);
        }

#if SIPV_ADS
        /// <summary>
        /// Shows a rewarded ad via <see cref="AdsManager"/> and grants the item's reward items once the
        /// ad reports a reward. Terminal result (success/failure) is delivered asynchronously through
        /// the ad show callbacks rather than synchronously, since Unity Ads/AdMob/Max/AppLovin rewarded
        /// flows are inherently callback-driven.
        /// </summary>
        private void BeginAdsPurchase(ConfigShopItem item, string placement, Action<ConfigShopItem> onSuccess,
            Action<string> onFailed)
        {
            var rewardItems = item.GetRewardItems();
            var settled = false;

            AdsManager.Instance.ShowRewardedAd(new RewardedAdsShowParameters(
                AdsType.Rewarded, placement, rewardItems,
                onDisplayFailed: failedEvent =>
                {
                    if (settled) return;
                    settled = true;
                    FailPurchase(item, placement, onFailed,
                        string.IsNullOrWhiteSpace(failedEvent?.message)
                            ? $"Failed to display rewarded ad for item '{item.Id}'."
                            : failedEvent.message);
                },
                onClose: _ =>
                {
                    if (settled) return;
                    settled = true;
                    FailPurchase(item, placement, onFailed, "Ad was closed before a reward was earned.");
                },
                onRewarded: _ =>
                {
                    if (settled) return;
                    settled = true;

                    if (!GrantItems(rewardItems))
                    {
                        FailPurchase(item, placement, onFailed, $"Failed to grant rewards for item '{item.Id}'.");
                        return;
                    }

                    IncreasePurchaseCounters(item);
                    DispatchResult(CreateResultEvent(item, ShopActionType.Purchase, placement, true,
                        string.Empty, string.Empty, true, false));
                    onSuccess?.Invoke(item);
                }));
        }
#endif

        private async UniTaskVoid RestorePurchasesInternalAsync(string placement, Action onSuccess,
            Action<string> onFailed)
        {
            var actionEvent = CreateActionEvent(string.Empty, ShopActionType.RestorePurchases, placement, true);
            DispatchAction(actionEvent);

            if (!await EnsureInitializedAsync())
            {
                DispatchResult(CreateResultEvent(null, ShopActionType.RestorePurchases, placement, false,
                    "Shop manager is not initialized.", string.Empty, false, false));
                onFailed?.Invoke("Shop manager is not initialized.");
                return;
            }

            if (_pendingRestoreRequest != null)
            {
                const string restoreInProgressError = "Restore purchases is already in progress.";
                DispatchResult(CreateResultEvent(null, ShopActionType.RestorePurchases, placement, false,
                    restoreInProgressError, string.Empty, false, false));
                onFailed?.Invoke(restoreInProgressError);
                return;
            }

            _pendingRestoreRequest = new RestorePurchasesRequest(placement, onSuccess, onFailed);
            if (!TryBeginRestorePurchases(_pendingRestoreRequest, out var error))
            {
                _pendingRestoreRequest = null;
                DispatchResult(CreateResultEvent(null, ShopActionType.RestorePurchases, placement, false,
                    error, string.Empty, false, false));
                onFailed?.Invoke(error);
            }
        }

        private bool TrySpendCostItems(GameItem[] costItems, out string error)
        {
            error = string.Empty;
            if (costItems == null || costItems.Length == 0) return true;

            if (UserDataManager.Instance == null || !UserDataManager.Instance.IsInitialized)
            {
                error = "UserDataManager is not initialized.";
                return false;
            }

            foreach (var costItem in costItems)
            {
                if (costItem == null || string.IsNullOrWhiteSpace(costItem.inventoryId) || costItem.amount <= 0) continue;

                var currentAmount = Inventory.Get<long>(costItem.inventoryId);
                if (currentAmount < costItem.amount)
                {
                    error = $"Not enough inventory '{costItem.inventoryId}'. Required {costItem.amount}, current {currentAmount}.";
                    return false;
                }
            }

            foreach (var costItem in costItems)
            {
                if (costItem == null || string.IsNullOrWhiteSpace(costItem.inventoryId) || costItem.amount <= 0) continue;
                Inventory.Remove(costItem.inventoryId, costItem.amount, SourcePurchaseSpend);
            }

            return true;
        }

        private bool GrantItems(GameItem[] items)
        {
            if (items == null || items.Length == 0) return true;
            if (UserDataManager.Instance == null || !UserDataManager.Instance.IsInitialized) return false;

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.inventoryId) || item.amount <= 0) continue;
                Inventory.Add(item.inventoryId, item.amount, SourcePurchaseReward);
            }

            return true;
        }

        private void IncreasePurchaseCounters(ConfigShopItem item)
        {
            var manager = UserDataManager.Instance;
            if (manager == null || !manager.IsInitialized || item == null) return;

            var totalKey = ShopKeys.PurchaseCount(item.Id);
            var totalCount = manager.Get<long>(totalKey) + 1;
            manager.Set(totalKey, totalCount);
            manager.Set(ShopKeys.LastPurchaseUnix(item.Id), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            if (item.Type == ShopItemType.Free && item.DailyFree > 0)
            {
                var dailyKey = ShopKeys.DailyPurchaseCount(item.Id, DateTime.UtcNow);
                var dailyCount = manager.Get<long>(dailyKey) + 1;
                manager.Set(dailyKey, dailyCount);
            }
        }

        private bool IsTransactionProcessed(string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId) || UserDataManager.Instance == null || !UserDataManager.Instance.IsInitialized)
                return false;

            return UserDataManager.Instance.Get<bool>(ShopKeys.ProcessedTransaction(transactionId));
        }

        private void MarkTransactionProcessed(string transactionId, string itemId)
        {
            if (string.IsNullOrWhiteSpace(transactionId) || UserDataManager.Instance == null || !UserDataManager.Instance.IsInitialized)
                return;

            UserDataManager.Instance.Set(ShopKeys.ProcessedTransaction(transactionId), true);
            UserDataManager.Instance.Set(ShopKeys.LastTransaction(itemId), transactionId);
        }

        private string ValidateCommonPurchaseRules(ConfigShopItem item, bool isIapPurchase)
        {
            if (item == null) return "Shop item was not found.";
            if (item.Type == ShopItemType.None) return $"Shop item '{item.Id}' has invalid type 'None'.";
            if (!item.IsAvailableAtUtc(DateTime.UtcNow)) return $"Shop item '{item.Id}' is not currently available.";
            if (item.ReachedMaxAmount())
                return $"Shop item '{item.Id}' has reached its maximum purchase limit.";
            if (item.IsTimeLimited && !item.IsAvailableAtUtc(DateTime.UtcNow))
                return $"Shop item '{item.Id}' is not available due to time-limited restrictions.";

            if (item.IsIap && string.IsNullOrWhiteSpace(item.SkuId))
                return $"Shop item '{item.Id}' is missing an IAP SKU config reference.";

            if (item.IsConversion)
            {
                if (item.CostItems.Length == 0)
                    return $"Conversion shop item '{item.Id}' requires at least one cost item.";
                if (item.GetRewardItems().Length == 0)
                    return $"Conversion shop item '{item.Id}' requires at least one reward item.";
            }

            if ((item.IsFree || item.IsAds) && item.GetRewardItems().Length == 0)
                return $"Shop item '{item.Id}' requires at least one reward item.";

            if (item.Type == ShopItemType.Free && item.DailyFree > 0)
            {
                var dailyCount = UserDataManager.Instance?.Get<long>(ShopKeys.DailyPurchaseCount(item.Id, DateTime.UtcNow)) ?? 0;
                if (dailyCount >= item.DailyFree)
                    return $"Shop item '{item.Id}' has reached its daily free limit.";
            }

            if (!isIapPurchase && item.IsConversion && item.CostItems.Length > 0)
            {
                foreach (var costItem in item.CostItems)
                {
                    if (costItem == null || string.IsNullOrWhiteSpace(costItem.inventoryId) || costItem.amount <= 0) continue;

                    var currentAmount = Inventory.Get<long>(costItem.inventoryId);
                    if (currentAmount < costItem.amount)
                        return $"Not enough inventory '{costItem.inventoryId}'. Required {costItem.amount}, current {currentAmount}.";
                }
            }

            return string.Empty;
        }

        private async UniTask<bool> EnsureInitializedAsync()
        {
            return IsInitialized || await Initialize();
        }

        private void FailPurchase(ConfigShopItem item, string placement, Action<string> onFailed, string error)
        {
            var resultEvent = CreateResultEvent(item, ShopActionType.Purchase, placement, false, error,
                string.Empty, false, false);
            DispatchResult(resultEvent);
            onFailed?.Invoke(resultEvent.errorMessage);
        }

        private static int ComparePriority(ConfigShopItem left, ConfigShopItem right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;

            var priorityCompare = left.Priority.CompareTo(right.Priority);
            return priorityCompare != 0 ? priorityCompare : string.CompareOrdinal(left.Id, right.Id);
        }

        private static ShopActionEvent CreateActionEvent(string itemId, ShopActionType actionType, string placement,
            bool isRestore)
        {
            return new ShopActionEvent
            {
                itemId = itemId,
                actionType = actionType,
                placement = placement ?? string.Empty,
                isRestore = isRestore,
                timestampUtc = DateTime.UtcNow
            };
        }

        private ShopResultEvent CreateResultEvent(ConfigShopItem item, ShopActionType actionType, string placement,
            bool success, string errorMessage, string transactionId, bool grantedRewards,
            bool alreadyProcessed)
        {
            return new ShopResultEvent
            {
                itemId = item?.Id ?? string.Empty,
                actionType = actionType,
                placement = placement ?? string.Empty,
                success = success,
                errorMessage = errorMessage ?? string.Empty,
                transactionId = transactionId ?? string.Empty,
                grantedRewards = grantedRewards,
                alreadyProcessed = alreadyProcessed,
                purchaseCount = item == null ? 0 : GetItemPurchaseCount(item.Id),
                timestampUtc = DateTime.UtcNow
            };
        }

        private static void DispatchAction(ShopActionEvent actionEvent)
        {
            EventManager.Invoke(EventShopAction, actionEvent);
            EventManager.Invoke(EventShopAction, GetTargetId(actionEvent.itemId, actionEvent.actionType), actionEvent);
        }

        private static void DispatchResult(ShopResultEvent resultEvent)
        {
            EventManager.Invoke(EventShopResult, resultEvent);
            EventManager.Invoke(EventShopResult, GetTargetId(resultEvent.itemId, resultEvent.actionType), resultEvent);
        }

        private static string GetTargetId(string itemId, ShopActionType actionType)
        {
            return actionType == ShopActionType.RestorePurchases || string.IsNullOrWhiteSpace(itemId)
                ? TargetRestorePurchases
                : itemId;
        }

        private static class ShopKeys
        {
            private const string Prefix = "shop";

            public static string PurchaseCount(string itemId) => $"{Prefix}.count.{itemId}";
            public static string LastPurchaseUnix(string itemId) => $"{Prefix}.last_purchase_unix.{itemId}";
            public static string LastTransaction(string itemId) => $"{Prefix}.last_transaction.{itemId}";
            public static string DailyPurchaseCount(string itemId, DateTime utcDate)
                => $"{Prefix}.daily_count.{itemId}.{utcDate:yyyyMMdd}";

            public static string ProcessedTransaction(string transactionId)
                => $"{Prefix}.processed_tx.{MakeSafeKey(transactionId)}";

            private static string MakeSafeKey(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return string.Empty;

                var bytes = Encoding.UTF8.GetBytes(raw);
                var builder = new StringBuilder(bytes.Length * 2);
                for (var i = 0; i < bytes.Length; i++)
                    builder.Append(bytes[i].ToString("x2"));

                return builder.ToString();
            }
        }

        protected sealed class PendingPurchaseRequest
        {
            public PendingPurchaseRequest(string itemId, string placement,
                Action<ConfigShopItem> onSuccess, Action<string> onFailed)
            {
                this.itemId = itemId;
                this.placement = placement ?? string.Empty;
                this.onSuccess = onSuccess;
                this.onFailed = onFailed;
            }

            public readonly string itemId;
            public readonly string placement;
            public readonly Action<ConfigShopItem> onSuccess;
            public readonly Action<string> onFailed;
        }

        protected sealed class RestorePurchasesRequest
        {
            public RestorePurchasesRequest(string placement, Action onSuccess, Action<string> onFailed)
            {
                this.placement = placement ?? string.Empty;
                this.onSuccess = onSuccess;
                this.onFailed = onFailed;
            }

            public readonly string placement;
            public readonly Action onSuccess;
            public readonly Action<string> onFailed;
        }
    }

    public enum ShopActionType
    {
        Purchase = 0,
        RestorePurchases = 1
    }

    public struct ShopActionEvent
    {
        public string itemId;
        public ShopActionType actionType;
        public string placement;
        public bool isRestore;
        public DateTime timestampUtc;
    }

    public struct ShopResultEvent
    {
        public string itemId;
        public ShopActionType actionType;
        public string placement;
        public bool success;
        public string errorMessage;
        public string transactionId;
        public bool grantedRewards;
        public bool alreadyProcessed;
        public long purchaseCount;
        public DateTime timestampUtc;
    }
}





