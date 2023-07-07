using HarmonyLib;
using TimberApi.ConsoleSystem;
using TimberApi.ModSystem;

namespace AutoRecipe
{
    [HarmonyPatch]
    public class AutoRecipe : IModEntrypoint
    {
        public void Entry(IMod mod, IConsoleWriter consoleWriter)
        {
            Harmony harmony = new Harmony("autorecipe");
            harmony.PatchAll();

            consoleWriter.LogInfo("Auto-recipe mod loaded");
        }
    }
}
