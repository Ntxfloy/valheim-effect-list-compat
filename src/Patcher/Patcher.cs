using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ValheimEffectListCompat
{
    public static class Patcher
    {
        private static readonly ManualLogSource Log = Logger.CreateLogSource("Ntxfloy EffectList Compat");
        private static bool resolverInstalled;

        public static IEnumerable<string> TargetDLLs => new[] { "assembly_valheim.dll" };

        public static void Patch(AssemblyDefinition assembly)
        {
            if (assembly == null || assembly.Name.Name != "assembly_valheim") return;
            try
            {
                TypeDefinition effectList = assembly.MainModule.GetType("EffectList");
                if (effectList == null)
                {
                    Log.LogWarning("EffectList type not found; no compatibility change applied.");
                    return;
                }

                MethodDefinition native = effectList.Methods.SingleOrDefault(IsNativeCreate);
                if (native == null)
                {
                    Log.LogWarning("Expected native EffectList.Create(..., ZDOID) overload not found; game API may have changed.");
                    return;
                }

                if (!effectList.Methods.Any(IsLegacyCreate))
                {
                    AddLegacyForwarder(effectList, native);
                    Log.LogInfo("Added only EffectList.Create(Vector3, Quaternion, Transform, Single, Int32) forwarder.");
                }
                else
                {
                    Log.LogInfo("Legacy EffectList.Create overload already exists; leaving assembly unchanged.");
                }

                InstallHarmonyResolver();
            }
            catch (Exception ex)
            {
                Log.LogError("EffectList compatibility patch failed safely: " + ex);
            }
        }

        private static bool IsNativeCreate(MethodDefinition method)
        {
            if (method.Name != "Create" || method.Parameters.Count != 6) return false;
            string[] expected =
            {
                "UnityEngine.Vector3", "UnityEngine.Quaternion", "UnityEngine.Transform",
                "System.Single", "System.Int32", "ZDOID"
            };
            for (int i = 0; i < expected.Length; i++)
                if (method.Parameters[i].ParameterType.FullName != expected[i]) return false;
            return true;
        }

        private static bool IsLegacyCreate(MethodDefinition method)
        {
            if (method.Name != "Create" || method.Parameters.Count != 5) return false;
            string[] expected =
            {
                "UnityEngine.Vector3", "UnityEngine.Quaternion", "UnityEngine.Transform",
                "System.Single", "System.Int32"
            };
            for (int i = 0; i < expected.Length; i++)
                if (method.Parameters[i].ParameterType.FullName != expected[i]) return false;
            return true;
        }

        private static void AddLegacyForwarder(TypeDefinition type, MethodDefinition native)
        {
            Mono.Cecil.MethodAttributes attributes = Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig;
            if (native.IsStatic) attributes |= Mono.Cecil.MethodAttributes.Static;
            var forwarder = new MethodDefinition("Create", attributes, native.ReturnType);
            for (int i = 0; i < 5; i++)
            {
                ParameterDefinition p = native.Parameters[i];
                forwarder.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            }

            Mono.Cecil.Cil.MethodBody body = forwarder.Body;
            body.InitLocals = true;
            body.MaxStackSize = 8;
            ILProcessor il = body.GetILProcessor();
            if (!native.IsStatic) il.Append(il.Create(OpCodes.Ldarg_0));
            for (int i = 0; i < 5; i++) il.Append(il.Create(OpCodes.Ldarg, forwarder.Parameters[i]));

            TypeReference finalType = native.Parameters[5].ParameterType;
            var defaultValue = new VariableDefinition(finalType);
            body.Variables.Add(defaultValue);
            il.Append(il.Create(OpCodes.Ldloca, defaultValue));
            il.Append(il.Create(OpCodes.Initobj, finalType));
            il.Append(il.Create(OpCodes.Ldloc, defaultValue));
            il.Append(il.Create(OpCodes.Call, native));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(forwarder);
        }

        private static void InstallHarmonyResolver()
        {
            if (resolverInstalled) return;
            MethodInfo declaredMethod = typeof(AccessTools).GetMethod(
                "DeclaredMethod",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) },
                null);
            MethodInfo prefix = typeof(Patcher).GetMethod(
                nameof(ResolveEffectListPatchTarget), BindingFlags.NonPublic | BindingFlags.Static);
            if (declaredMethod == null || prefix == null)
            {
                Log.LogWarning("Harmony resolver hook could not be installed; by-name patches may remain ambiguous.");
                return;
            }

            var harmony = new Harmony("Ntxfloy.ValheimEffectListCompat");
            harmony.Patch(declaredMethod, prefix: new HarmonyMethod(prefix));
            resolverInstalled = true;
            Log.LogInfo("Harmony by-name lookup selects the original EffectList.Create overload by metadata order.");
        }

        private static bool ResolveEffectListPatchTarget(
            Type type, string name, Type[] parameters, Type[] generics, ref MethodInfo __result)
        {
            if (type == null || type.Name != "EffectList" ||
                type.Assembly.GetName().Name != "assembly_valheim" ||
                name != "Create" || parameters != null)
                return true;

            MethodInfo[] candidates = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "Create")
                .OrderBy(m => m.MetadataToken)
                .ToArray();

            if (candidates.Length != 2 || candidates[0].MetadataToken >= candidates[1].MetadataToken)
                return true;
            __result = candidates[0];
            return false;
        }
    }
}