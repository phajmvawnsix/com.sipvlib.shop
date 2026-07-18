using System;
using SiPVLib.Config;
using SiPVLib.Config.Configs;
using SiPVLib.UserData;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif
using UnityEngine;
using UnityEngine.Purchasing;

namespace SiPVLib.Shop.Configs
{
    /// <summary>
    /// Configuration for a shop item with pricing, rewards, and UI settings.
    /// Supports IAP, conversion, ads, and free shop item types.
    /// </summary>
#if ODIN_INSPECTOR
    [InfoBox("Configure shop item properties including type, pricing, rewards, and UI settings.")]
#endif
    public class ConfigShopItem : GameConfig
    {
        [SerializeField]
        [Tooltip("The type of shop item (IAP, Conversion, Ads, Free).")]
        protected ShopItemType _type;

        [SerializeField]
        [Tooltip("Maximum amount settings for this shop item.")]
        protected MaxAmountSettings _maxAmountSettings;

        [SerializeField]
        [Tooltip("Display priority in shop (lower value = higher priority).")]
#if ODIN_INSPECTOR
        [PropertyRange(0, 1000)]
#endif
        protected int _priority;

        [SerializeField]
        [Tooltip("Whether this item has a time limit.")]
        protected bool _isTimeLimited;

        [SerializeField]
#if ODIN_INSPECTOR
        [ShowIf(nameof(_isTimeLimited))]
#endif
        [Tooltip("Start time for time-limited items (Unix timestamp in seconds).")]
        protected long _startTime;

        [SerializeField]
#if ODIN_INSPECTOR
        [ShowIf(nameof(_isTimeLimited))]
#endif
        [Tooltip("End time for time-limited items (Unix timestamp in seconds).")]
        protected long _endTime;

        [SerializeField]
        [Tooltip("Whether users can double rewards for this item (ad-related).")]
        protected bool _isCanDoubleReward;

        [SerializeField]
#if ODIN_INSPECTOR
        [ShowIf(nameof(_type), ShopItemType.Free)]
#endif
        [Tooltip("Number of free claims per day for free shop items.")]
#if ODIN_INSPECTOR
        [PropertyRange(0, 100)]
#endif
        protected int _dailyFree;

        [SerializeField]
#if ODIN_INSPECTOR
        [ShowIf(nameof(_type), ShopItemType.IAP)]
#endif
        [Tooltip("Unity IAP product type (Consumable, NonConsumable, Subscription).")]
        protected ProductType _iapProductType = ProductType.Consumable;

        [SerializeField]
#if ODIN_INSPECTOR
        [ShowIf(nameof(_type), ShopItemType.IAP)]
#endif
        [ConfigRef(typeof(ConfigIAPSku))]
        [Tooltip("SKU ID reference for IAP products. Must reference a ConfigIAPSku config.")]
        protected string _skuId;

        [SerializeField]
        [Tooltip("Items required to purchase this shop item (costs/currency).")]
        protected GameItem[] _costItems;

        [SerializeField]
        [Tooltip("Rewards given when purchasing this shop item.")]
        protected GameItem[] _items;

        [SerializeField]
#if ODIN_INSPECTOR
        [ShowIf(nameof(_type), ShopItemType.Free)]
#endif
        [Tooltip("Special rewards for daily free claims on free shop items.")]
        protected GameItem[] _dailyFreeItems;

        [SerializeField]
        [Tooltip("UI display settings for this shop item (category, sale badge, etc.).")]
        protected ShopItemUISettings _uiSettings;

        public ShopItemType Type => _type;
        public MaxAmountSettings MaxAmountSettings => _maxAmountSettings;
        public int Priority => _priority;
        public bool IsTimeLimited => _isTimeLimited;
        public bool IsCanDoubleReward => _isCanDoubleReward;
        public int DailyFree => _dailyFree;
        public long StartTime => _startTime;
        public long EndTime => _endTime;
        public ProductType IapProductType => _iapProductType;
        public string SkuId => _skuId;
        public GameItem[] CostItems => _costItems ?? Array.Empty<GameItem>();
        public GameItem[] Items => _items ?? Array.Empty<GameItem>();
        public GameItem[] DailyFreeItems => _dailyFreeItems ?? Array.Empty<GameItem>();
        public ShopItemUISettings UISettings => _uiSettings;
        public bool IsIap => _type == ShopItemType.IAP;
        public bool IsConversion => _type == ShopItemType.Conversion;
        public bool IsAds => _type == ShopItemType.Ads;
        public bool IsFree => _type == ShopItemType.Free;

        public bool IsAvailableAtUtc(DateTime utcNow)
        {
            if (!_isTimeLimited) return true;

            var unixTime = new DateTimeOffset(utcNow).ToUnixTimeSeconds();
            if (_startTime > 0 && unixTime < _startTime) return false;
            if (_endTime > 0 && unixTime > _endTime) return false;

            return true;
        }

        public GameItem[] GetRewardItems()
        {
            if (_type == ShopItemType.Free && _dailyFreeItems != null && _dailyFreeItems.Length > 0)
                return _dailyFreeItems;

            return _items ?? Array.Empty<GameItem>();
        }

        public bool ReachedMaxAmount()
        {
            return UserDataManager.Instance.ReachedMaxAmount(Id, _maxAmountSettings);
        }
    }
}
