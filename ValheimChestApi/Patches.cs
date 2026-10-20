using HarmonyLib;

namespace ValheimChestApi
{
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class ZNetScene_Awake_Patch
    {
        private static void Postfix(ZNetScene __instance)
        {
            ChestRegistry.Instance.BuildContainerPrefabIndex(__instance);
        }
    }

    // Covers both initial placement/load and whenever the container is instantiated as a zone loads.
    [HarmonyPatch(typeof(Container), "Awake")]
    internal static class Container_Awake_Patch
    {
        private static void Postfix(Container __instance)
        {
            ChestRegistry.Instance.OnContainerLoaded(__instance);
        }
    }

    // Container.Save() runs whenever its inventory changes (item added/removed/moved) and periodically -
    // this is our cheap way to keep the snapshot fresh without polling every container on every request.
    [HarmonyPatch(typeof(Container), "Save")]
    internal static class Container_Save_Patch
    {
        private static void Postfix(Container __instance)
        {
            ChestRegistry.Instance.OnContainerLoaded(__instance);
        }
    }

    [HarmonyPatch(typeof(Container), "OnDestroy")]
    internal static class Container_OnDestroy_Patch
    {
        private static void Prefix(Container __instance)
        {
            ChestRegistry.Instance.OnContainerUnloaded(__instance);
        }
    }
}
