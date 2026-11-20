using BepInEx;
using BepInEx.Configuration;
using CardiacSimCore;
using CUCoreLib;
using HarmonyLib;
using UnityEngine;

namespace RealisticEcgMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CUCoreLibPlugin.GUID, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class RealisticEcgPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.casualties.realistic.ecg";
        public const string PluginName = "Realistic Cardiac & ECG Mod";
        public const string PluginVersion = "1.0.0";

        private Harmony? _harmony;
        private ConfigEntry<FrontalLead>? _defaultLeadConfig;

        private void Awake()
        {
            _defaultLeadConfig = Config.Bind(
                "ECG",
                "DefaultFrontalLead",
                FrontalLead.II,
                "Default frontal plane ECG lead displayed by the monitor (I, II, III, aVR, aVL, aVF).");

            CardiacModManager.DefaultLead = _defaultLeadConfig.Value;
            _defaultLeadConfig.SettingChanged += (_, _) =>
            {
                CardiacModManager.DefaultLead = _defaultLeadConfig.Value;
            };

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(CardiacHarmonyPatches));

            HookAmiodaroneLiquid();
            HookEpinephrineLiquid();
            CUCoreIntegration.RegisterAll(Logger);
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded (Default Lead: {CardiacModManager.DefaultLead}).");
        }

        private void HookEpinephrineLiquid()
        {
            if (Liquids.Registry != null && Liquids.Registry.TryGetValue("epinephrine", out var epiLiquid))
            {
                epiLiquid.onHealthUse = (ml, limb) =>
                {
                    if (limb == null || limb.body == null) return;
                    CardiacModManager.AdministerEpinephrine(limb.body, ml, intravenous: true);
                };
                epiLiquid.onDrink = (ml, body) =>
                {
                    if (body == null) return;
                    CardiacModManager.AdministerEpinephrine(body, ml, intravenous: false);
                };
                Logger.LogInfo("Patched Liquids.Registry[\"epinephrine\"] to fill endogenous adrenaline (to 100) then overflow into uncapped exogenous catecholamines.");
            }
            else
            {
                Logger.LogWarning("Could not find 'epinephrine' in Liquids.Registry during Awake.");
            }
        }

        private void HookAmiodaroneLiquid()
        {
            if (Liquids.Registry != null && Liquids.Registry.TryGetValue("amiodarone", out var amioLiquid))
            {
                amioLiquid.onHealthUse = (ml, limb) =>
                {
                    if (limb == null || limb.body == null) return;
                    CardiacModManager.AdministerAmiodarone(limb.body, ml);

                    // Keep mild local tissue irritation if desired, without touching fibrillationProgress
                    float mult = ml / 20f;
                    limb.body.limbs[0].muscleHealth -= 0.10f * mult;
                    limb.body.limbs[1].muscleHealth -= 0.10f * mult;
                    limb.body.limbs[2].muscleHealth -= 0.10f * mult;
                };
                Logger.LogInfo("Patched Liquids.Registry[\"amiodarone\"] to use 5-minute half-life PK model.");
            }
            else
            {
                Logger.LogWarning("Could not find 'amiodarone' in Liquids.Registry during Awake.");
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            CardiacModManager.DisposeAll();
        }
    }
}
