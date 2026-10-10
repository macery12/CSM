using CSM.BaseGame.Helpers;
using HarmonyLib;
using UnityEngine;

namespace CSM.BaseGame.Injections
{
    [HarmonyPatch(typeof(TreeManager), "BurnTree")]
    public class HostTreeIgnitionPatch
    {
        public static bool Prefix(ref bool __result)
        {
            if (!WildfireSync.IsClient)
                return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(TreeManager), "FireSimulationStep")]
    public class HostTreeFireSimulationPatch
    {
        public static bool Prefix(ref bool __result)
        {
            if (!WildfireSync.IsClient)
                return true;
            // Keep the entry until the host sends its recovery or removal.
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(FireCopterAI), "ExtinguishFire",
        new[] { typeof(ushort), typeof(Vehicle), typeof(uint), typeof(TreeInstance) },
        new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Ref })]
    public class HostTreeExtinguishingPatch
    {
        public static bool Prefix(ref TreeInstance treeData, ref bool __result)
        {
            if (!WildfireSync.IsClient)
                return true;
            TreeManager manager = TreeManager.instance;
            Vector3 position = treeData.Position;
            __result = true;
            for (int i = 0; i < manager.m_burningTrees.m_size; i++)
            {
                TreeManager.BurningTree fire = manager.m_burningTrees.m_buffer[i];
                if (fire.m_fireIntensity == 0 || fire.m_treeIndex == 0 || fire.m_treeIndex >= manager.m_trees.m_buffer.Length ||
                    manager.m_trees.m_buffer[fire.m_treeIndex].GrowState == 0)
                    continue;
                Vector3 offset = manager.m_trees.m_buffer[fire.m_treeIndex].Position - position;
                if (offset.x * offset.x + offset.z * offset.z < 1024f)
                {
                    __result = false;
                    break;
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(NaturalResourceManager), "SimulationStepImpl2")]
    public class BurnedGroundSimulationPatch
    {
        public static void Postfix(uint simulationFrame)
        {
            WildfireSync.ResourceStep(simulationFrame);
        }
    }

    [HarmonyPatch(typeof(NaturalResourceManager), "TryDumpResource",
        new[] { typeof(NaturalResourceManager.Resource), typeof(int), typeof(int), typeof(Vector3), typeof(float), typeof(bool) })]
    public class HostBurnedGroundPatch
    {
        public static bool Prefix(NaturalResourceManager.Resource resource, ref int __result)
        {
            if (!WildfireSync.IsClient || resource != NaturalResourceManager.Resource.Burned)
                return true;
            __result = 0;
            return false;
        }

        public static void Postfix(NaturalResourceManager.Resource resource, Vector3 position, float radius)
        {
            if (resource == NaturalResourceManager.Resource.Burned)
                WildfireSync.ObserveGroundArea(position, radius);
        }
    }

    [HarmonyPatch(typeof(TreeManager), "CreateTree")]
    public class WildfireTreeCreatePatch
    {
        public static void Postfix(bool __result, uint tree)
        {
            if (__result)
                WildfireSync.Invalidate(tree);
        }
    }

    [HarmonyPatch(typeof(TreeManager), "ReleaseTreeImplementation")]
    public class WildfireTreeReleasePatch
    {
        public static void Prefix(uint tree)
        {
            WildfireSync.Invalidate(tree);
        }
    }

    [HarmonyPatch(typeof(TreeManager), "MoveTree")]
    public class WildfireTreeMovePatch
    {
        public static void Postfix(uint tree)
        {
            WildfireSync.Invalidate(tree);
        }
    }
}
