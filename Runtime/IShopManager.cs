using System;
using Cysharp.Threading.Tasks;
using SiPVLib.Shop.Configs;

namespace SiPVLib.Shop
{
    /// <summary>
    /// Public contract for a shop manager: item lookup, pricing, purchasing, and restore-purchases.
    /// </summary>
    /// <seealso cref="ShopManager{T}"/>
    /// <seealso cref="UnityShopManager"/>
    public interface IShopManager
    {
        /// <summary>
        /// Loads shop item configs and initializes the underlying store connection (if any).
        /// Safe to call multiple times; subsequent calls are no-ops once initialized.
        /// </summary>
        /// <returns>True if initialization succeeded.</returns>
        public UniTask<bool> Initialize();

        /// <summary>Looks up a shop item config by id.</summary>
        /// <returns>The matching <see cref="ConfigShopItem"/>, or null if not found.</returns>
        public ConfigShopItem GetItem(string itemId);

        /// <summary>Gets the IAP price for an item in its default/store currency.</summary>
        public decimal GetItemIAPPrice(string itemId);

        /// <summary>Gets the IAP price for an item only if it matches the given currency, otherwise 0.</summary>
        public decimal GetItemIAPPrice(string itemId, string currency);

        /// <summary>Gets the ISO currency code for an item's IAP price.</summary>
        public string GetItemIAPCurrency(string itemId);

        /// <summary>Gets the number of times an item has been successfully purchased by the current user.</summary>
        public long GetItemPurchaseCount(string itemId);

        /// <summary>Returns all loaded shop item configs, sorted by priority.</summary>
        public ConfigShopItem[] GetAllItems();

        /// <summary>
        /// Starts a purchase for the given item id (IAP, Conversion, Ads, or Free depending on item type).
        /// Fire-and-forget; result is delivered via <paramref name="onSuccess"/>/<paramref name="onFailed"/>
        /// and broadcast globally via <see cref="ShopManager{T}.EventShopResult"/>.
        /// </summary>
        /// <param name="itemId">Id of the <see cref="ConfigShopItem"/> to purchase.</param>
        /// <param name="placement">Caller-defined UI placement/context, included in broadcast events.</param>
        public void PurchaseItem(string itemId, string placement, Action<ConfigShopItem> onSuccess = null, Action<string> onFailed = null);

        /// <summary>
        /// Restores previously completed purchases (e.g. after reinstall). Fire-and-forget; result is
        /// delivered via <paramref name="onSuccess"/>/<paramref name="onFailed"/> and broadcast via
        /// <see cref="ShopManager{T}.EventShopResult"/> with <see cref="ShopActionType.RestorePurchases"/>.
        /// </summary>
        /// <param name="placement">Caller-defined UI placement/context, included in broadcast events.</param>
        public void RestorePurchases(string placement, Action onSuccess = null, Action<string> onFailed = null);
    }
}
