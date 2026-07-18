using System;

namespace SiPVLib.Shop.Configs
{
    /// <summary>
    /// Enumeration of shop item types available in the shop system.
    /// </summary>
    [Serializable]
    public enum ShopItemType
    {
        /// <summary>Unspecified shop item type.</summary>
        None,
        
        /// <summary>In-App Purchase (IAP) product - paid item.</summary>
        IAP,
        
        /// <summary>Conversion shop item - exchange in-game currency for rewards.</summary>
        Conversion,
        
        /// <summary>Advertisement-based reward - watch ad to get rewards.</summary>
        Ads,
        
        /// <summary>Free daily reward - limited free claims per day.</summary>
        Free,
    }
}