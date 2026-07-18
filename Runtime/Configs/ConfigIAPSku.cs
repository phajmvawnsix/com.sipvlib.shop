using SiPVLib.Config.Configs;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif
using UnityEngine;

namespace SiPVLib.Shop.Configs
{
    /// <summary>
    /// Configuration for IAP (In-App Purchase) SKU identifiers and prices per platform.
    /// </summary>
#if ODIN_INSPECTOR
    [InfoBox("Define platform-specific SKU IDs and prices for IAP products.")]
#endif
    public class ConfigIAPSku : GameConfig
    {
        [SerializeField]
        [Tooltip("Android SKU identifier from Google Play Console.")]
        protected string _androidSku;

        [SerializeField]
        [Tooltip("iOS SKU identifier from App Store Connect.")]
        protected string _iosSku;

        [SerializeField]
        [Tooltip("Sale price for Android in USD.")]
#if ODIN_INSPECTOR
        [PropertyRange(0.01f, 999.99f)]
#endif
        protected float _androidPrice;

        [SerializeField]
        [Tooltip("Sale price for iOS in USD.")]
#if ODIN_INSPECTOR
        [PropertyRange(0.01f, 999.99f)]
#endif
        protected float _iosPrice;

        public string AndroidSku => _androidSku;
        public string IOSSku => _iosSku;
        public float AndroidPrice => _androidPrice;
        public float IOSPrice => _iosPrice;

        public string GetSku(RuntimePlatform platform)
        {
            return platform switch
            {
                RuntimePlatform.Android => _androidSku,
                RuntimePlatform.IPhonePlayer => _iosSku,
                RuntimePlatform.OSXPlayer => _iosSku,
                RuntimePlatform.OSXEditor => _iosSku,
                _ => string.IsNullOrWhiteSpace(_androidSku) ? _iosSku : _androidSku
            };
        }

        public decimal GetPrice(RuntimePlatform platform)
        {
            return platform switch
            {
                RuntimePlatform.Android => (decimal)_androidPrice,
                RuntimePlatform.IPhonePlayer => (decimal)_iosPrice,
                RuntimePlatform.OSXPlayer => (decimal)_iosPrice,
                RuntimePlatform.OSXEditor => (decimal)_iosPrice,
                _ => _androidPrice > 0f ? (decimal)_androidPrice : (decimal)_iosPrice
            };
        }
    }
}
