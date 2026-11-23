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
        public const string PluginVersion = "1.1.0";

        internal static RealisticEcgPlugin? Instance { get; private set; }

        private Harmony? _harmony;
        private ConfigEntry<FrontalLead>? _defaultLeadConfig;
        private ConfigEntry<float>? _monitorPosXConfig;
        private ConfigEntry<float>? _monitorPosYConfig;
        private ConfigEntry<bool>? _monitorEnabledConfig;

        private void Awake()
        {
            Instance = this;

            _defaultLeadConfig = Config.Bind(
                "ECG",
                "DefaultFrontalLead",
                FrontalLead.II,
                "Default frontal plane ECG lead displayed by the monitor (I, II, III, aVR, aVL, aVF).");

            _monitorPosXConfig = Config.Bind(
                "MonitorOverlay",
                "WindowPosX",
                -24f,
                "Saved horizontal anchored position of the draggable real-time physiology monitor window.");

            _monitorPosYConfig = Config.Bind(
                "MonitorOverlay",
                "WindowPosY",
                -90f,
                "Saved vertical anchored position of the draggable real-time physiology monitor window.");

            _monitorEnabledConfig = Config.Bind(
                "MonitorOverlay",
                "WindowEnabled",
                false,
                "Whether the real-time physiology monitor overlay automatically opens whenever the biochip medical panel is opened.");

            CardiacModManager.DefaultLead = _defaultLeadConfig.Value;
            _defaultLeadConfig.SettingChanged += (_, _) =>
            {
                CardiacModManager.DefaultLead = _defaultLeadConfig.Value;
            };

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(CardiacHarmonyPatches));

            HookAmiodaroneLiquid();
            HookEpinephrineLiquid();
            HookNitroprussideLiquid();
            HookVasopressinLiquid();
            CUCoreIntegration.RegisterAll(Logger);
            PhysiologyMonitorApi.RegisterBuiltInArterialPressureCurve();
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded (Default Lead: {CardiacModManager.DefaultLead}).");
        }

        internal static Vector2 GetSavedMonitorOverlayPosition()
        {
            if (Instance?._monitorPosXConfig != null && Instance._monitorPosYConfig != null)
            {
                return new Vector2(Instance._monitorPosXConfig.Value, Instance._monitorPosYConfig.Value);
            }
            return new Vector2(-24f, -90f);
        }

        internal static void SaveMonitorOverlayPosition(Vector2 anchoredPosition)
        {
            if (Instance?._monitorPosXConfig == null || Instance._monitorPosYConfig == null) return;
            Instance._monitorPosXConfig.Value = anchoredPosition.x;
            Instance._monitorPosYConfig.Value = anchoredPosition.y;
            Instance.Config.Save();
        }

        internal static bool GetSavedMonitorOverlayEnabled()
        {
            return Instance?._monitorEnabledConfig != null && Instance._monitorEnabledConfig.Value;
        }

        internal static void SaveMonitorOverlayEnabled(bool enabled)
        {
            if (Instance?._monitorEnabledConfig == null) return;
            if (Instance._monitorEnabledConfig.Value == enabled) return;
            Instance._monitorEnabledConfig.Value = enabled;
            Instance.Config.Save();
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

        private void HookNitroprussideLiquid()
        {
            if (Liquids.Registry != null && Liquids.Registry.TryGetValue("sodiumnitroprusside", out var nitroLiquid))
            {
                nitroLiquid.onHealthUse = (ml, limb) =>
                {
                    if (limb?.body == null) return;
                    CardiacModManager.AdministerNitroprusside(limb.body, ml, intravenous: true);
                };
                nitroLiquid.onDrink = (ml, body) =>
                {
                    if (body == null) return;
                    CardiacModManager.AdministerNitroprusside(body, ml, intravenous: false);
                };
                Logger.LogInfo("Patched Liquids.Registry[\"sodiumnitroprusside\"] with dose-dependent vasodilator PK (t1/2 = 50s).");
            }
            else
            {
                Logger.LogWarning("Could not find 'sodiumnitroprusside' in Liquids.Registry during Awake.");
            }
        }

        private void HookVasopressinLiquid()
        {
            if (Liquids.Registry != null && Liquids.Registry.TryGetValue("vasopressin", out var vasoLiquid))
            {
                vasoLiquid.onHealthUse = (ml, limb) =>
                {
                    if (limb?.body == null) return;
                    CardiacModManager.AdministerVasopressin(limb.body, ml, intravenous: true);
                };
                vasoLiquid.onDrink = (ml, body) =>
                {
                    if (body == null) return;
                    CardiacModManager.AdministerVasopressin(body, ml, intravenous: false);
                };
                Logger.LogInfo("Patched Liquids.Registry[\"vasopressin\"] (抗利尿激素) with pure dose-dependent V1 vasoconstrictor PK (t1/2 = 120s).");
            }
            else
            {
                Logger.LogWarning("Could not find 'vasopressin' in Liquids.Registry during Awake.");
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            PhysiologyMonitorOverlay.DestroyInstance();
            CardiacModManager.DisposeAll();
        }
    }
}
