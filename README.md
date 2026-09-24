# Valheim Compatibility Bridges

[![Thunderstore Version](https://img.shields.io/badge/Thunderstore-v0.1.6-blue.svg)](https://thunderstore.io/c/valheim/p/Ntxfloy/ValheimEffectListCompat/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Target: Valheim 1.0 (Unity 6)](https://img.shields.io/badge/Valheim-1.0%20(Unity%206)-orange.svg)](https://valheimgame.com/)

A lightweight, robust two-component compatibility mod designed to restore legacy mod compatibility and eliminate game-breaking `NullReferenceException` cascades in **Valheim 1.0 (Unity 6)**.

---

## The Problems This Mod Solves

When Iron Gate upgraded Valheim to **1.0 (Unity 6000)**, two critical breaking changes broke dozens of existing mods and destabilized sector unloading:

### 1. `MissingMethodException` on `EffectList.Create`
In Valheim 1.0, Iron Gate added a 6th parameter (`ZDOID targetZDO`) to `EffectList.Create`:
```csharp
// Legacy signature (used by dozens of existing mods):
EffectList.Create(Vector3 pos, Quaternion rot, Transform baseChild, float scale, int itemVariant)

// Valheim 1.0 signature:
EffectList.Create(Vector3 pos, Quaternion rot, Transform baseChild, float scale, int itemVariant, ZDOID targetZDO)
```
Any mod compiled against older versions of Valheim throws:
```text
System.MissingMethodException: Method not found: EffectList.Create
```

### 2. `AmbiguousMatchException` in Harmony
When multiple overloads exist without proper parameter discrimination, Harmony lookups by method name fail with:
```text
System.Reflection.AmbiguousMatchException: Ambiguous match found for EffectList.Create
```

### 3. Unity 6 `ZNetScene.RemoveObjects` NRE Cascade (5,000+ Errors per Session)
In Unity 6, native C++ object destruction and managed reference lifetime behavior changed. When objects leave player sectors, `ZNetScene.RemoveObjects` iterates `m_instances.Values`. If an external mod destroyed a `ZNetView` or its associated `GameObject` during gameplay, vanilla code dereferences the dead object:
```text
NullReferenceException: Object reference not set to an instance of an object
  at ZNetScene.RemoveObjects (System.Collections.Generic.List`1[ZDO] currentNearObjects, System.Collections.Generic.List`1[ZDO] currentDistantObjects)
  at ZNetScene.CreateDestroyObjects ()
  at ZNetScene.Update ()
```
This causes an infinite loop of thousands of NREs every frame, severe stuttering, memory leaks, and broken entity despawning.

---

## How It Works

This package provides a clean, two-layer architecture:

```
 Valheim Game Launch
         │
         ├──► 1. Preloader Patcher (ValheimEffectListCompat.dll)
         │       ├─ Injected via Mono.Cecil before game assemblies load
         │       ├─ Adds legacy 5-argument EffectList.Create forwarder
         │       └─ Installs Harmony resolver hook for by-name patches
         │
         └──► 2. Runtime Scene Plugin (ValheimSceneCompat.dll)
                 ├─ Intercepts ZNetScene.RemoveObjects
                 ├─ Phase 1: Cleans dead/corrupted keys from m_instances
                 ├─ Phase 2: Safely unloads entities leaving active sector
                 ├─ Phase 3: Cleans up non-persistent owned ZDOs in ZDOMan
                 └─ Installs safety Finalizers on CreateDestroyObjects & RemoveObjects
```

### Component 1: Cecil Preloader (`ValheimEffectListCompat.dll`)
* **Location:** `BepInEx/patchers/Ntxfloy-ValheimEffectListCompat/ValheimEffectListCompat.dll`
* **Target:** `netstandard2.0` with **zero** dependencies on game assemblies.
* **EffectList Forwarder:** Injects a lightweight IL wrapper that forwards 5-argument calls to the native 6-argument method with a default `ZDOID.None`.
* **Harmony Resolver:** Automatically directs ambiguous Harmony `EffectList.Create` patches to the primary game implementation by metadata token order.

### Component 2: Runtime Scene Plugin (`ValheimSceneCompat.dll`)
* **Location:** `BepInEx/plugins/Ntxfloy-ValheimSceneCompat/ValheimSceneCompat.dll`
* **Target:** `netstandard2.1` standard `BaseUnityPlugin`.
* **Safe Dictionary Sanitization:** Sweeps and removes null/destroyed keys and dead references before iterating.
* **Guaranteed Removal:** `m_instances.Remove(zdo)` is guaranteed in a `finally` block, ensuring clean sector transitions.
* **Vanilla Parity for Non-Persistent ZDOs:** Correctly unregisters temporary owned entities (arrows, projectiles, transient VFX) via `ZDOMan.DestroyZDO` inside an isolated block, preventing world database leaks.
* **Safety Finalizers:** Catches and suppresses any rogue exceptions in `CreateDestroyObjects` and `RemoveObjects`, ensuring the game's main loop never aborts.
* **Smart Error Throttling:** Logs the first occurrence of each unique exception signature immediately with full stack trace, then throttles subsequent occurrences to once per 10 seconds.

---

## Configuration

Settings are saved in `BepInEx/config/ntxfloy.valheimscenecompat.cfg`:

| Category | Option | Default | Description |
| :--- | :--- | :---: | :--- |
| `ZNetScene` | `EnableSafeRemoveObjects` | `true` | Enables dictionary sanitization and guaranteed removal in `ZNetScene.RemoveObjects`. |
| `ZNetScene` | `EnableCreateDestroyFinalizer` | `true` | Installs a safety finalizer on `ZNetScene.CreateDestroyObjects` to catch unhandled third-party exceptions. |
| `ZNetScene` | `PreventDoubleZNetViewGhostZDO` | `false` | Experimental: Destroys duplicate `ZNetView` components on prefabs rather than creating unmanaged ghost ZDOs. |

---

## Installation

### Via r2modman / Thunderstore Mod Manager (Recommended)
1. Install [r2modman](https://valheim.thunderstore.io/package/ebkr/r2modman/) or Thunderstore App.
2. Search for `ValheimEffectListCompat` by `Ntxfloy` and click **Download**.

### Manual Installation
1. Ensure [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) is installed.
2. Download and extract `ValheimEffectListCompat-0.1.5.zip`.
3. Copy the `BepInEx` folder from the archive directly into your Valheim game directory:
   - Patcher goes to: `Valheim/BepInEx/patchers/Ntxfloy-ValheimEffectListCompat/ValheimEffectListCompat.dll`
   - Plugin goes to: `Valheim/BepInEx/plugins/Ntxfloy-ValheimSceneCompat/ValheimSceneCompat.dll`

---

## Building from Source

Requires [.NET SDK](https://dotnet.microsoft.com/download) and Valheim game references.

```bash
# Clone the repository
git clone https://github.com/Ntxfloy/valheim-effect-list-compat.git
cd valheim-effect-list-compat

# Build the solution
dotnet build -c Release

# Or package for Thunderstore
powershell -ExecutionPolicy Bypass -File .\package.ps1
```

---

## License

This project is licensed under the [MIT License](LICENSE).