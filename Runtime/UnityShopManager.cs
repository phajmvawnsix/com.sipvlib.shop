using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using SiPVLib.Debugging;
using SiPVLib.Shop.Configs;
using UnityEngine;
using UnityEngine.Purchasing;

namespace SiPVLib.Shop
{
    /// <summary>
    /// Concrete <see cref="ShopManager{T}"/> backed by Unity IAP v5 (<see cref="StoreController"/>).
    /// Bridges the store's callback-driven API into UniTask via <see cref="UniTaskCompletionSource{T}"/>
    /// during initialization, and implements restore-purchases via <c>StoreController.RestoreTransactions</c>.
    /// </summary>
    public class UnityShopManager : ShopManager<UnityShopManager>
    {
        private readonly Dictionary<string, Product> _productsByItemId = new(StringComparer.Ordinal);
        private StoreController _storeController;
        private UniTaskCompletionSource<bool> _initializeTcs;
        private bool _isRestoreInProgress;

        /// <summary>Connects to Unity IAP v5 and fetches configured products. Returns true immediately if no IAP items are configured.</summary>
        protected override async UniTask<bool> OnInitializeAsync()
        {
            if (_storeController != null) return true;
            if (_initializeTcs != null) return await _initializeTcs.Task;

            var productDefinitions = BuildProductDefinitions();
            if (productDefinitions.Count == 0)
            {
                CustomLog.Log("[UnityShopManager] No IAP products configured. Shop initialized without store bindings.");
                return true;
            }

            _storeController = UnityIAPServices.StoreController();
            RegisterCallbacks(_storeController);

            try
            {
                await _storeController.Connect();
            }
            catch (Exception ex)
            {
                CustomLog.LogException(ex);
                UnregisterCallbacks(_storeController);
                _storeController = null;
                return false;
            }

            _initializeTcs = new UniTaskCompletionSource<bool>();
            _storeController.FetchProducts(productDefinitions);
            return await _initializeTcs.Task;
        }

        /// <inheritdoc/>
        public override decimal GetItemIAPPrice(string itemId)
        {
            return TryGetLiveIapInfo(itemId, out var livePrice, out _)
                ? livePrice
                : base.GetItemIAPPrice(itemId);
        }

        /// <inheritdoc/>
        public override string GetItemIAPCurrency(string itemId)
        {
            return TryGetLiveIapInfo(itemId, out _, out var currency)
                ? currency
                : base.GetItemIAPCurrency(itemId);
        }

        /// <summary>Reads live price/currency from the cached Unity IAP <see cref="Product"/> metadata for the item.</summary>
        protected override bool TryGetLiveIapInfo(string itemId, out decimal price, out string currency)
        {
            price = 0m;
            currency = string.Empty;

            if (string.IsNullOrWhiteSpace(itemId)) return false;
            if (!_productsByItemId.TryGetValue(itemId, out var product) || product?.metadata == null) return false;

            price = product.metadata.localizedPrice;
            currency = product.metadata.isoCurrencyCode ?? string.Empty;
            return price > 0m || !string.IsNullOrWhiteSpace(currency);
        }

        /// <summary>Starts the Unity IAP purchase flow. Result arrives asynchronously via <see cref="OnPurchasePending"/>/<see cref="OnPurchaseConfirmed"/>.</summary>
        protected override bool TryBeginIapPurchase(ConfigShopItem item, PendingPurchaseRequest request, out string error)
        {
            error = string.Empty;

            if (_storeController == null)
            {
                error = "Unity IAP v5 store controller is not initialized.";
                return false;
            }

            if (!_productsByItemId.TryGetValue(item.Id, out var product) || product == null)
            {
                error = $"Unity IAP product '{item.Id}' is not available.";
                return false;
            }

            if (!product.availableToPurchase)
            {
                error = $"Unity IAP product '{item.Id}' is not available to purchase.";
                return false;
            }

            _storeController.PurchaseProduct(product);
            return true;
        }

        /// <summary>Starts <c>StoreController.RestoreTransactions</c>. Aggregate completion arrives via that callback; individual restored items still flow through <see cref="OnPurchasePending"/>.</summary>
        protected override bool TryBeginRestorePurchases(RestorePurchasesRequest request, out string error)
        {
            error = string.Empty;

            if (_storeController == null)
            {
                error = "Unity IAP v5 store controller is not initialized.";
                return false;
            }

            _isRestoreInProgress = true;
            _storeController.RestoreTransactions((success, message) =>
            {
                _isRestoreInProgress = false;
                CompleteRestorePurchases(success, message);
            });
            return true;
        }

        private List<ProductDefinition> BuildProductDefinitions()
        {
            _productsByItemId.Clear();
            var productDefinitions = new List<ProductDefinition>();
            var items = GetAllItems();

            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null || !item.IsIap) continue;

                if (!TryGetIapSku(item.Id, out var skuConfig))
                {
                    CustomLog.LogWarning($"[UnityShopManager] Missing IAP SKU config for item '{item.Id}'.");
                    continue;
                }

                var storeProductId = skuConfig.GetSku(Application.platform);
                if (string.IsNullOrWhiteSpace(storeProductId))
                {
                    CustomLog.LogWarning($"[UnityShopManager] No platform SKU available for item '{item.Id}' on '{Application.platform}'.");
                    continue;
                }

                productDefinitions.Add(new ProductDefinition(item.Id, storeProductId, item.IapProductType));
            }

            return productDefinitions;
        }

        private void RegisterCallbacks(StoreController storeController)
        {
            storeController.OnProductsFetched += OnProductsFetched;
            storeController.OnProductsFetchFailed += OnProductsFetchFailed;
            storeController.OnPurchasePending += OnPurchasePending;
            storeController.OnPurchaseConfirmed += OnPurchaseConfirmed;
            storeController.OnStoreDisconnected += OnStoreDisconnected;
        }

        private void UnregisterCallbacks(StoreController storeController)
        {
            if (storeController == null) return;

            storeController.OnProductsFetched -= OnProductsFetched;
            storeController.OnProductsFetchFailed -= OnProductsFetchFailed;
            storeController.OnPurchasePending -= OnPurchasePending;
            storeController.OnPurchaseConfirmed -= OnPurchaseConfirmed;
            storeController.OnStoreDisconnected -= OnStoreDisconnected;
        }

        /// <remarks>Not thread-safe: assumes Unity IAP v5 dispatches <c>StoreController</c> callbacks on the Unity main thread, so <c>_productsByItemId</c> is accessed without locks.</remarks>
        private void OnProductsFetched(List<Product> products)
        {
            CacheProducts(products);
            _initializeTcs?.TrySetResult(true);
            _initializeTcs = null;

            CustomLog.Log($"[UnityShopManager] Unity IAP v5 fetched {_productsByItemId.Count} product(s).");
        }

        /// <remarks>Not thread-safe: assumes it is called on the Unity main thread (see <see cref="OnProductsFetched"/>).</remarks>
        private void OnProductsFetchFailed(ProductFetchFailed failure)
        {
            CacheProducts(_storeController.GetProducts().ToList());

            var message = failure == null
                ? "Unknown product fetch failure."
                : $"{failure.FailureReason}. Failed products: {failure.FailedFetchProducts.Count}";
            CustomLog.LogWarning($"[UnityShopManager] {message}");

            var hasAnyProducts = _productsByItemId.Count > 0;
            _initializeTcs?.TrySetResult(hasAnyProducts);
            _initializeTcs = null;
        }

        /// <remarks>Not thread-safe: assumes it is called on the Unity main thread (see <see cref="OnProductsFetched"/>).</remarks>
        private void OnPurchasePending(PendingOrder order)
        {
            var itemId = GetItemId(order);
            if (string.IsNullOrWhiteSpace(itemId))
            {
                CustomLog.LogError("[UnityShopManager] Pending purchase received with an unknown item id.");
                _storeController?.ConfirmPurchase(order);
                return;
            }

            HandleIapPurchaseSucceeded(itemId, order.Info.TransactionID,
                _isRestoreInProgress || !HasPendingPurchase(itemId));

            _storeController?.ConfirmPurchase(order);
        }

        /// <remarks>Not thread-safe: assumes it is called on the Unity main thread (see <see cref="OnProductsFetched"/>).</remarks>
        private void OnPurchaseConfirmed(Order order)
        {
            if (order is FailedOrder failedOrder)
            {
                HandleIapPurchaseFailed(GetItemId(failedOrder),
                    $"{failedOrder.FailureReason}: {failedOrder.Details}");
            }
        }

        /// <remarks>
        /// Not thread-safe: assumes it is called on the Unity main thread (see <see cref="OnProductsFetched"/>).
        /// Note: no reconnect/retry is attempted here; a disconnect is only logged.
        /// </remarks>
        private void OnStoreDisconnected(StoreConnectionFailureDescription failure)
        {
            if (failure == null) return;
            CustomLog.LogWarning($"[UnityShopManager] Store disconnected: {failure.message}");
        }

        private void CacheProducts(IEnumerable<Product> products)
        {
            _productsByItemId.Clear();
            if (products == null) return;

            foreach (var product in products)
            {
                var itemId = product?.definition?.id;
                if (string.IsNullOrWhiteSpace(itemId)) continue;
                _productsByItemId[itemId] = product;
            }
        }

        private static string GetItemId(Order order)
        {
            if (order == null) return string.Empty;

            var firstItem = order.CartOrdered.Items().FirstOrDefault();
            return firstItem == null ? string.Empty : firstItem.Product.definition.id;
        }
    }
}
