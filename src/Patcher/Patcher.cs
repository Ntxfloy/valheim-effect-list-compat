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

        public static IEnumerable<string> TargetDLLs => new[] { "assembly_valheim.dll", "OdinStorage.dll" };

        public static void Patch(AssemblyDefinition assembly)
        {
            if (assembly == null) return;

            if (assembly.Name.Name == "assembly_valheim")
                PatchValheim(assembly);
            else if (assembly.Name.Name == "OdinStorage")
                PatchOdinStorage(assembly);
        }

        // ===================== assembly_valheim patching =====================

        private static void PatchValheim(AssemblyDefinition assembly)
        {
            try
            {
                // ---- EffectList.Create compatibility ----
                TypeDefinition effectList = assembly.MainModule.GetType("EffectList");
                if (effectList == null)
                {
                    Log.LogWarning("EffectList type not found; no compatibility change applied.");
                }
                else
                {
                    MethodDefinition nativeCreate = effectList.Methods.SingleOrDefault(IsNativeCreate);
                    if (nativeCreate == null)
                    {
                        Log.LogWarning("Expected native EffectList.Create(..., ZDOID) overload not found; game API may have changed.");
                    }
                    else if (!effectList.Methods.Any(IsLegacyCreate))
                    {
                        AddEffectListLegacyForwarder(effectList, nativeCreate);
                        Log.LogInfo("Added EffectList.Create(Vector3, Quaternion, Transform, Single, Int32) forwarder.");
                    }
                    else
                    {
                        Log.LogInfo("Legacy EffectList.Create overload already exists; leaving unchanged.");
                    }

                    InstallHarmonyResolver();
                }

                // ---- Character.Message compatibility ----
                TypeDefinition character = assembly.MainModule.GetType("Character");
                if (character == null)
                {
                    Log.LogWarning("Character type not found in assembly_valheim; skipping Character.Message patch.");
                }
                else
                {
                    MethodDefinition nativeMessage = character.Methods.FirstOrDefault(IsNativeCharacterMessage);
                    if (nativeMessage == null)
                    {
                        Log.LogWarning("Expected 5-arg Character.Message not found; game API may have changed.");
                    }
                    else if (!character.Methods.Any(IsLegacyCharacterMessage))
                    {
                        AddCharacterMessageForwarder(character, nativeMessage, assembly.MainModule);
                        Log.LogInfo("Added Character.Message(MessageType, string, int, Sprite) forwarder -> 5-arg version.");
                    }
                    else
                    {
                        Log.LogInfo("Legacy Character.Message(4 args) already exists; leaving unchanged.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError("assembly_valheim compatibility patch failed: " + ex);
            }
        }

        // ===================== OdinStorage.dll IL patching =====================

        /// <summary>
        /// Rewrites every callvirt/call to Character::Message(MessageType,String,Int32,Sprite) [4-arg]
        /// -> Character::Message(MessageType,String,Int32,Sprite,Boolean) [5-arg] + ldc.i4.0 (false).
        /// Done at preload time via Cecil so Mono never sees the broken 4-arg callsite.
        /// </summary>
        private static void PatchOdinStorage(AssemblyDefinition assembly)
        {
            try
            {
                int totalFixed = 0;

                foreach (TypeDefinition type in assembly.MainModule.GetTypes())
                {
                    foreach (MethodDefinition method in type.Methods)
                    {
                        if (!method.HasBody) continue;

                        var instructions = method.Body.Instructions;
                        var il = method.Body.GetILProcessor();

                        for (int i = 0; i < instructions.Count; i++)
                        {
                            Instruction instr = instructions[i];
                            if (instr.OpCode != OpCodes.Callvirt && instr.OpCode != OpCodes.Call)
                                continue;

                            var target = instr.Operand as MethodReference;
                            if (target == null) continue;
                            if (!IsOdinStorage4ArgCharacterMessage(target)) continue;

                            // Build 5-arg replacement reference
                            MethodReference fiveArgRef = Build5ArgCharacterMessageRef(assembly.MainModule, target);
                            if (fiveArgRef == null) continue;

                            // Insert ldc.i4.0 (false) BEFORE the callvirt
                            var pushFalse = il.Create(OpCodes.Ldc_I4_0);
                            il.InsertBefore(instr, pushFalse);

                            // Update the call to point to 5-arg version
                            instr.Operand = fiveArgRef;

                            totalFixed++;
                            i++; // skip the inserted instruction
                        }
                    }
                }

                if (totalFixed > 0)
                    Log.LogInfo($"OdinStorage.dll: rewrote {totalFixed} Character.Message(4-arg) callsites to 5-arg.");
                else
                    Log.LogWarning("OdinStorage.dll: no 4-arg Character.Message callsites found (already patched or API changed).");
            }
            catch (Exception ex)
            {
                Log.LogError("OdinStorage.dll IL patch failed: " + ex);
            }
        }

        private static bool IsOdinStorage4ArgCharacterMessage(MethodReference method)
        {
            if (method.Name != "Message") return false;
            if (method.Parameters.Count != 4) return false;
            string declType = method.DeclaringType?.FullName ?? "";
            if (declType != "Character" && !declType.EndsWith(".Character")) return false;
            string p0 = method.Parameters[0].ParameterType.FullName;
            string p1 = method.Parameters[1].ParameterType.FullName;
            string p2 = method.Parameters[2].ParameterType.FullName;
            string p3 = method.Parameters[3].ParameterType.FullName;
            return (p0.Contains("MessageType") || p0.Contains("MessageHud"))
                && p1 == "System.String"
                && p2 == "System.Int32"
                && p3.Contains("Sprite");
        }

        private static MethodReference Build5ArgCharacterMessageRef(ModuleDefinition module, MethodReference original4)
        {
            try
            {
                var voidType = module.TypeSystem.Void;
                var boolType = module.TypeSystem.Boolean;

                var method5 = new MethodReference("Message", voidType, original4.DeclaringType)
                {
                    HasThis = original4.HasThis,
                    ExplicitThis = original4.ExplicitThis,
                    CallingConvention = original4.CallingConvention,
                };

                foreach (var p in original4.Parameters)
                    method5.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));

                method5.Parameters.Add(new ParameterDefinition("log", Mono.Cecil.ParameterAttributes.None, boolType));

                return module.ImportReference(method5);
            }
            catch
            {
                return null;
            }
        }

        // ===================== EffectList helpers =====================

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

        private static void AddEffectListLegacyForwarder(TypeDefinition type, MethodDefinition native)
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

        // ===================== Character.Message helpers =====================

        private static bool IsNativeCharacterMessage(MethodDefinition method)
        {
            if (method.Name != "Message" || method.Parameters.Count != 5) return false;
            string[] expected =
            {
                "MessageHud/MessageType", "System.String", "System.Int32",
                "UnityEngine.Sprite", "System.Boolean"
            };
            for (int i = 0; i < expected.Length; i++)
                if (method.Parameters[i].ParameterType.FullName != expected[i]) return false;
            return true;
        }

        private static bool IsLegacyCharacterMessage(MethodDefinition method)
        {
            if (method.Name != "Message" || method.Parameters.Count != 4) return false;
            string[] expected =
            {
                "MessageHud/MessageType", "System.String", "System.Int32", "UnityEngine.Sprite"
            };
            for (int i = 0; i < expected.Length; i++)
                if (method.Parameters[i].ParameterType.FullName != expected[i]) return false;
            return true;
        }

        private static void AddCharacterMessageForwarder(TypeDefinition type, MethodDefinition native, ModuleDefinition module)
        {
            var attrs = Mono.Cecil.MethodAttributes.Public
                | Mono.Cecil.MethodAttributes.HideBySig
                | Mono.Cecil.MethodAttributes.Virtual
                | Mono.Cecil.MethodAttributes.NewSlot;

            var forwarder = new MethodDefinition("Message", attrs, module.TypeSystem.Void);

            for (int i = 0; i < 4; i++)
            {
                ParameterDefinition p = native.Parameters[i];
                forwarder.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            }

            var body = forwarder.Body;
            body.InitLocals = false;
            body.MaxStackSize = 7;
            ILProcessor il = body.GetILProcessor();

            il.Append(il.Create(OpCodes.Ldarg_0));
            for (int i = 0; i < 4; i++)
                il.Append(il.Create(OpCodes.Ldarg, forwarder.Parameters[i]));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Callvirt, native));
            il.Append(il.Create(OpCodes.Ret));

            type.Methods.Add(forwarder);
        }

        // ===================== Harmony resolver (for EffectList) =====================

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
