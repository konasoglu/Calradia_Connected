using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace CalradiaConnected.Missions.Arena.Patches
{
    [HarmonyPatch(typeof(Agent), "RegisterBlow")]
    public static class AgentRegisterBlowPatch
    {
        public static void Prefix(Agent __instance, ref Blow blow)
        {
            try
            {
                ArenaDuelManager.Instance.InterceptDuelBlow(__instance, ref blow);
            }
            catch
            {
                // Never allow harmony prefix exceptions to break native engine combat
            }
        }
    }
}

