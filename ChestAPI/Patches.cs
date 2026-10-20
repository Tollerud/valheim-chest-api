using HarmonyLib;
using UnityEngine;

namespace ChestAPI
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

            // Container declares no OnDestroy of its own (only OnDestroyed, which fires just when a chest is
            // broken in-game), so there is nothing to patch for "zone unloaded". Attach a tiny tracker instead
            // and let Unity's own OnDestroy tell us.
            if (__instance.GetComponent<ContainerUnloadTracker>() == null)
            {
                __instance.gameObject.AddComponent<ContainerUnloadTracker>();
            }
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

    internal sealed class ContainerUnloadTracker : MonoBehaviour
    {
        private void OnDestroy()
        {
            var container = GetComponent<Container>();
            if (container != null)
            {
                ChestRegistry.Instance.OnContainerUnloaded(container);
            }
        }
    }
}
