using System;
using SiPVLib.Config;
using SiPVLib.Pool.Config;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif
using UnityEngine;

namespace SiPVLib.Shop.Configs
{
    /// <summary>
    /// UI display settings for shop items in the shop interface.
    /// </summary>
    [Serializable]
#if ODIN_INSPECTOR
    [InfoBox("Configure how this shop item appears in the UI (category, badges, custom pool).")]
#endif
    public class ShopItemUISettings
    {
        [SerializeField]
        [Tooltip("Category/section name for grouping shop items in UI.")]
        protected string _category;

        [SerializeField]
        [Tooltip("Show a sale/discount badge on this item.")]
        protected bool _isSaleOff;

        [SerializeField]
#if ODIN_INSPECTOR
        [ShowIf(nameof(_isSaleOff))]
#endif
        [Tooltip("Discount percentage to display (0-100).")]
#if ODIN_INSPECTOR
        [PropertyRange(0, 100)]
#endif
        protected long _saleOffPercent = 20;

        [SerializeField]
        [Tooltip("Mark this item as popular/featured with a badge.")]
        protected bool _isPopular;

        [SerializeField]
        [Tooltip("Mark this item as best value with a badge.")]
        protected bool _isBestValue;

        [SerializeField]
        [ConfigRef(typeof(PoolConfig))]
        [Tooltip("Optional custom UI pool ID for a custom shop item prefab. Leave empty to use default.")]
        protected string _customUIPoolId;

        public string Category => _category;
        public bool IsSaleOff => _isSaleOff;
        public long SaleOffPercent => _saleOffPercent;
        public bool IsPopular => _isPopular;
        public bool IsBestValue => _isBestValue;
        public string CustomUIPoolId => _customUIPoolId;
    }
}
