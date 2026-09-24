using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValheimSceneCompat
{
    [BepInPlugin("ntxfloy.valheimscenecompat", "Valheim Scene Compatibility", "0.1.5")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static readonly List<ZDO> s_deadKeys = new List<ZDO>(64);

        private static readonly ErrorThrottle s_removeObjectsThrottle = new ErrorThrottle("SceneCompat:RemoveObjects");
        private static readonly ErrorThrottle s_createDestroyThrottle = new ErrorThrottle("SceneCompat:CreateDestroyObjects");
        private static readonly ErrorThrottle s_doubleZNetViewThrottle = new ErrorThrottle("DoubleZNetview Guard");

        public static ConfigEntry<bool> EnableSafeRemoveObjects;
        public static ConfigEntry<bool> EnableCreateDestroyFinalizer;
        public static ConfigEntry<bool> PreventDoubleZNetViewGhostZDO;

        private void Awake()
        {
            Log = Logger;

            EnableSafeRemoveObjects = Config.Bind(
                "ZNetScene", "EnableSafeRemoveObjects", true,
                "Enables dictionary sanitization and guaranteed removal in ZNetScene.RemoveObjects, preventing Unity 6 NRE cascades."
            );

            EnableCreateDestroyFinalizer = Config.Bind(
                "ZNetScene", "EnableCreateDestroyFinalizer", true,
                "Installs a safety Finalizer on ZNetScene.CreateDestroyObjects to catch and suppress unhandled exceptions, preventing the main loop from aborting."
            );

            PreventDoubleZNetViewGhostZDO = Config.Bind(
                "ZNetScene", "PreventDoubleZNetViewGhostZDO", false,
                "Experimental: Destroys duplicate ZNetViews on instantiated prefabs instead of creating unmanaged ghost ZDOs in ZDOMan. Default is false."
            );

            var harmony = new Harmony("ntxfloy.valheimscenecompat");

            if (EnableSafeRemoveObjects.Value)
            {
                Type znetSceneType = typeof(ZNetScene);
                MethodInfo removeObjects = znetSceneType.GetMethod(
                    "RemoveObjects",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(List<ZDO>), typeof(List<ZDO>) },
                    null);

                FieldInfo instancesField = znetSceneType.GetField("m_instances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo tempRemovedField = znetSceneType.GetField("m_tempRemoved", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (removeObjects != null && instancesField != null && tempRemovedField != null &&
                    instancesField.FieldType == typeof(Dictionary<ZDO, ZNetView>) &&
                    tempRemovedField.FieldType == typeof(List<ZNetView>))
                {
                    MethodInfo prefix = typeof(Plugin).GetMethod(nameof(RemoveObjectsPrefix), BindingFlags.Public | BindingFlags.Static);
                    MethodInfo finalizer = typeof(Plugin).GetMethod(nameof(RemoveObjectsFinalizer), BindingFlags.Public | BindingFlags.Static);

                    harmony.Patch(removeObjects, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
                    Log.LogInfo("Hooked ZNetScene.RemoveObjects with safe dictionary sanitization, guaranteed removal, and finalizer.");
                }
                else
                {
                    Log.LogWarning("ZNetScene.RemoveObjects signature or fields changed; safe RemoveObjects patch skipped safely.");
                }
            }

            if (EnableCreateDestroyFinalizer.Value)
            {
                Type znetSceneType = typeof(ZNetScene);
                MethodInfo createDestroy = znetSceneType.GetMethod(
                    "CreateDestroyObjects",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);

                if (createDestroy != null)
                {
                    MethodInfo finalizer = typeof(Plugin).GetMethod(nameof(CreateDestroyObjectsFinalizer), BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(createDestroy, finalizer: new HarmonyMethod(finalizer));
                    Log.LogInfo("Installed safety Finalizer on ZNetScene.CreateDestroyObjects.");
                }
            }

            if (PreventDoubleZNetViewGhostZDO.Value)
            {
                Type znetViewType = typeof(ZNetView);
                MethodInfo awake = znetViewType.GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (awake != null)
                {
                    MethodInfo awakePrefix = typeof(Plugin).GetMethod(nameof(ZNetViewAwakePrefix), BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(awake, prefix: new HarmonyMethod(awakePrefix));
                    Log.LogInfo("Installed DoubleZNetview duplicate suppression on ZNetView.Awake.");
                }
            }
        }

        public static bool RemoveObjectsPrefix(
            ZNetScene __instance,
            Dictionary<ZDO, ZNetView> ___m_instances,
            List<ZNetView> ___m_tempRemoved,
            List<ZDO> currentNearObjects,
            List<ZDO> currentDistantObjects)
        {
            if (___m_instances == null || ___m_tempRemoved == null)
                return true; // Fallback to vanilla if instance state is uninitialized

            byte earmark = (byte)(Time.frameCount & 255);

            if (currentNearObjects != null)
            {
                for (int i = 0; i < currentNearObjects.Count; i++)
                {
                    ZDO zdo = currentNearObjects[i];
                    if (zdo != null) zdo.TempRemoveEarmark = earmark;
                }
            }

            if (currentDistantObjects != null)
            {
                for (int i = 0; i < currentDistantObjects.Count; i++)
                {
                    ZDO zdo = currentDistantObjects[i];
                    if (zdo != null) zdo.TempRemoveEarmark = earmark;
                }
            }

            ___m_tempRemoved.Clear();

            // Phase 1: Dictionary sanitization
            s_deadKeys.Clear();
            foreach (var kvp in ___m_instances)
            {
                ZDO key = kvp.Key;
                ZNetView view = kvp.Value;

                // Unity operator == checks native C++ pointer; ReferenceEquals checks managed object
                if (key == null || ReferenceEquals(view, null) || view == null)
                {
                    s_deadKeys.Add(key);
                    continue;
                }

                ZDO viewZdo = null;
                try
                {
                    viewZdo = view.GetZDO();
                }
                catch (Exception ex)
                {
                    s_removeObjectsThrottle.Log("Error retrieving ZDO from ZNetView during sanitization", ex);
                }

                if (viewZdo == null || !ReferenceEquals(viewZdo, key))
                {
                    s_deadKeys.Add(key);
                    continue;
                }

                // Phase 2: Healthy candidates for sector removal
                if (viewZdo.TempRemoveEarmark != earmark)
                {
                    ___m_tempRemoved.Add(view);
                }
            }

            // Remove dead keys collected during sanitization (NEVER call DestroyZDO here: only remove from dictionary!)
            for (int i = 0; i < s_deadKeys.Count; i++)
            {
                ZDO deadKey = s_deadKeys[i];
                if (deadKey != null)
                {
                    ___m_instances.Remove(deadKey);
                }
            }
            s_deadKeys.Clear();

            // Phase 3: Guaranteed removal of objects out of sector range
            for (int i = 0; i < ___m_tempRemoved.Count; i++)
            {
                ZNetView view = ___m_tempRemoved[i];
                if (ReferenceEquals(view, null)) continue;

                ZDO zdo = null;
                try
                {
                    zdo = view.GetZDO();
                    view.ResetZDO();

                    // 1. Non-persistent owned ZDOs (e.g. projectiles, transient VFX/spawns) must be destroyed in ZDOMan.
                    // Placed BEFORE touching Unity GameObject to ensure cleanup even if GameObject access throws.
                    // Wrapped in dedicated try-catch so foreign ZDO issues never prevent GameObject destruction or dictionary removal.
                    if (zdo != null)
                    {
                        try
                        {
                            if (!zdo.Persistent && zdo.IsOwner())
                            {
                                ZDOMan.instance?.DestroyZDO(zdo);
                            }
                        }
                        catch (Exception ex)
                        {
                            s_removeObjectsThrottle.Log("Error checking persistence or destroying non-persistent ZDO in RemoveObjects", ex);
                        }
                    }

                    // 2. Destroy native Unity GameObject safely
                    if (view != null)
                    {
                        GameObject go = null;
                        try
                        {
                            go = view.gameObject;
                        }
                        catch (Exception ex)
                        {
                            s_removeObjectsThrottle.Log("Component.gameObject threw on destroyed entity in RemoveObjects", ex);
                        }

                        if (go != null)
                        {
                            UnityEngine.Object.Destroy(go);
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_removeObjectsThrottle.Log("Error resetting or destroying ZNetView in RemoveObjects", ex);
                }
                finally
                {
                    // Guaranteed removal from m_instances
                    if (zdo != null)
                    {
                        ___m_instances.Remove(zdo);
                    }
                }
            }

            return false; // Skip vanilla method execution
        }

        public static Exception RemoveObjectsFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                s_removeObjectsThrottle.Log("Unhandled exception in RemoveObjects", __exception);
                return null; // Suppress exception to protect caller
            }
            return null;
        }

        public static Exception CreateDestroyObjectsFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                s_createDestroyThrottle.Log("External exception in CreateDestroyObjects", __exception);
                return null; // Suppress exception so Update loop is never aborted
            }
            return null;
        }

        public static bool ZNetViewAwakePrefix(ZNetView __instance)
        {
            if (!PreventDoubleZNetViewGhostZDO.Value) return true;

            // When a prefab is instantiated from network ZDO, the root consumes m_initZDO.
            // Any secondary ZNetView in the hierarchy sees m_useInitZDO == true but m_initZDO == null.
            if (ZNetView.m_useInitZDO && ZNetView.m_initZDO == null)
            {
                try
                {
                    string name = __instance?.gameObject != null ? __instance.gameObject.name : "unknown";
                    s_doubleZNetViewThrottle.Log($"Prevented ghost ZDO creation on duplicate ZNetView on {name}. Destroying duplicate component.", null);

                    if (__instance != null)
                    {
                        // Disable component immediately so it does not run updates in the remaining frame
                        __instance.enabled = false;
                        UnityEngine.Object.Destroy(__instance);
                    }
                }
                catch (Exception ex)
                {
                    s_doubleZNetViewThrottle.Log("Error in ZNetViewAwakePrefix duplicate suppression", ex);
                }
                return false;
            }

            return true;
        }

        private class ErrorThrottle
        {
            private const int MaxTrackedSignatures = 200;
            private readonly string _tag;
            private readonly Dictionary<string, ErrorEntry> _entries = new Dictionary<string, ErrorEntry>(32);
            private ErrorEntry _overflowEntry;

            private class ErrorEntry
            {
                public float LastLogTime;
                public int SuppressedCount;
            }

            public ErrorThrottle(string tag)
            {
                _tag = tag;
            }

            public void Log(string context, Exception ex)
            {
                string sig = GetSignature(context, ex);
                float now = Time.unscaledTime;

                if (!_entries.TryGetValue(sig, out ErrorEntry entry))
                {
                    if (_entries.Count < MaxTrackedSignatures)
                    {
                        entry = new ErrorEntry { LastLogTime = now, SuppressedCount = 0 };
                        _entries[sig] = entry;
                    }
                    else
                    {
                        if (_overflowEntry == null)
                        {
                            _overflowEntry = new ErrorEntry { LastLogTime = now, SuppressedCount = 0 };
                        }
                        entry = _overflowEntry;
                    }

                    string details = ex != null ? $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : context;
                    Plugin.Log?.LogWarning($"[{_tag}] [First occurrence] {context}: {details}");
                    return;
                }

                entry.SuppressedCount++;
                if (now - entry.LastLogTime >= 10f)
                {
                    entry.LastLogTime = now;
                    string details = ex != null ? $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : context;
                    Plugin.Log?.LogWarning($"[{_tag}] [{entry.SuppressedCount} events throttled] {context}: {details}");
                    entry.SuppressedCount = 0;
                }
            }

            private static string GetSignature(string context, Exception ex)
            {
                if (ex == null)
                    return context ?? "unknown";

                string stack = ex.StackTrace;
                string topFrame = "no_stack";
                if (!string.IsNullOrEmpty(stack))
                {
                    int newline = stack.IndexOfAny(new[] { '\r', '\n' });
                    topFrame = newline > 0 ? stack.Substring(0, newline).Trim() : stack.Trim();
                }

                return $"{ex.GetType().Name} @ {topFrame} ({context})";
            }
        }
    }
}