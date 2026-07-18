# com.sipvlib.shop

Part of [SiPVLib](https://github.com/phajmvawnsix/SiPVLib). A store-agnostic in-game shop layer (`ShopManager`/`IShopManager`) on top of Unity IAP v5, supporting IAP, currency-conversion, rewarded-ad, and free shop items with purchase/restore orchestration, reward granting, and event broadcasting.

## Install

Add to your project's `Packages/manifest.json`:

```json
"com.sipvlib.shop": "https://github.com/phajmvawnsix/com.sipvlib.shop.git",
"com.sipvlib.ads": "https://github.com/phajmvawnsix/com.sipvlib.ads.git",
"com.sipvlib.config": "https://github.com/phajmvawnsix/com.sipvlib.config.git",
"com.sipvlib.debugging": "https://github.com/phajmvawnsix/com.sipvlib.debugging.git",
"com.sipvlib.event": "https://github.com/phajmvawnsix/com.sipvlib.event.git",
"com.sipvlib.pool": "https://github.com/phajmvawnsix/com.sipvlib.pool.git",
"com.sipvlib.userdata": "https://github.com/phajmvawnsix/com.sipvlib.userdata.git",
"com.sipvlib.utilities": "https://github.com/phajmvawnsix/com.sipvlib.utilities.git",
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask"
```

UPM does not automatically resolve nested git dependencies — you must add the `com.sipvlib.*` and UniTask entries above yourself alongside this package. `com.unity.purchasing` resolves automatically from Unity's package registry.

## Optional: Odin Inspector

This package integrates with [Odin Inspector](https://odininspector.com) (Sirenix) if you have it installed, but does NOT require it and does NOT bundle it — Odin is a paid Unity Asset Store asset and cannot be redistributed here.

- **Without Odin installed**: `ConfigIAPSku`, `ConfigShopItem`, and `ShopItemUISettings` work fully with plain Unity Inspector rendering — no field grouping, conditional visibility, or range sliders.
- **With Odin installed** (purchase + import from the Asset Store, which auto-defines the `ODIN_INSPECTOR` scripting define symbol): the same classes light up Odin's `InfoBox`, `ShowIf`, and `PropertyRange` attributes.

No manual setup is needed beyond installing Odin itself — detection is automatic via the `ODIN_INSPECTOR` define.

## Documentation
- [Usage guide](USAGE.md) — original module documentation carried over from the SiPVLib monolith
