# Changelog

## [1.0.1] - 2026-08-09

Make com.sipvlib.ads optional. ShopManager compiled unconditional hard reference to Ads types (AdsManager, RewardedAdsShowParameters, AdsType), causing compile error when com.sipvlib.ads not installed. Ads-dependent code now gated behind SIPV_ADS scripting define (auto-set via asmdef versionDefines when com.sipvlib.ads is present); Ads shop item purchases fail gracefully with a clear error when the package is missing. com.sipvlib.ads removed from package.json hard dependencies — install it manually to enable Ads shop items.

## [1.0.0] - 2026-07-18

Initial extraction from SiPVLib monolith.
