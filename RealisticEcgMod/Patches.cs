using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CardiacSimCore;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RealisticEcgMod
{
    internal sealed class CardiacWorkerLifetimeHook : MonoBehaviour
    {
        public Body? TargetBody;

        private void OnDestroy()
        {
            if (TargetBody != null)
            {
                CardiacModManager.DisposeBody(TargetBody);
            }
        }
    }

    internal sealed class EcgVisualizerRenderState
    {
        public Texture2D? Tex;
        public RawImage? Img;
        public Color32[] Buffer = new Color32[0];
        public int Width;
        public int Height;
        public int WriteX;
        public int LastY;
        public float TimeAccum;
        public float FadeAccum = 1f;
        public int LastLeadVersion = -1;
        public readonly FrontalEcgSample[] ComaStripBuffer = new FrontalEcgSample[1400];
        public int ComaStripCount;
    }

    internal sealed class UnconsciousSpeedState
    {
        public PlayerCamera.SpeedType? ManualOverride;
    }

    internal sealed class WoundViewLeadUi
    {
        public Button? LeadButton;
        public TextMeshProUGUI? LeadLabel;
        public Button? MultiLeadButton;
        public Button? MonitorOverlayButton;
        public Image? MonitorOverlayBg;
        public bool Initialized;
    }

    internal sealed class ManualDefibLeadUi
    {
        public RectTransform? ButtonRect;
        public TextMeshProUGUI? Label;
    }

    [HarmonyPatch]
    internal static class CardiacHarmonyPatches
    {
        private static readonly ConditionalWeakTable<ECGVisualizer, EcgVisualizerRenderState> EcgStates =
            new ConditionalWeakTable<ECGVisualizer, EcgVisualizerRenderState>();

        private static readonly ConditionalWeakTable<PlayerCamera, UnconsciousSpeedState> UnconsciousSpeedStates =
            new ConditionalWeakTable<PlayerCamera, UnconsciousSpeedState>();

        private static readonly ConditionalWeakTable<WoundView, WoundViewLeadUi> WoundViewLeadUis =
            new ConditionalWeakTable<WoundView, WoundViewLeadUi>();

        private static readonly ConditionalWeakTable<ManualDefibMinigame, ManualDefibLeadUi> ManualDefibLeadUis =
            new ConditionalWeakTable<ManualDefibMinigame, ManualDefibLeadUi>();

        private static readonly AccessTools.FieldRef<ManualDefibMinigame, float> ManualChargeRef =
            AccessTools.FieldRefAccess<ManualDefibMinigame, float>("currentCharge");

        private static readonly AccessTools.FieldRef<ManualDefibMinigame, List<TextMeshProUGUI>> ManualTextsRef =
            AccessTools.FieldRefAccess<ManualDefibMinigame, List<TextMeshProUGUI>>("texts");

        private static readonly AccessTools.FieldRef<ManualDefibMinigame, ECGVisualizer> ManualEcgRef =
            AccessTools.FieldRefAccess<ManualDefibMinigame, ECGVisualizer>("ecg");

        private static readonly AccessTools.FieldRef<AEDMinigame, int> AedStateRef =
            AccessTools.FieldRefAccess<AEDMinigame, int>("state");

        private static readonly AccessTools.FieldRef<AEDMinigame, float> AedTimerRef =
            AccessTools.FieldRefAccess<AEDMinigame, float>("timer");

        private static readonly AccessTools.FieldRef<AEDMinigame, TextMeshProUGUI> AedTextRef =
            AccessTools.FieldRefAccess<AEDMinigame, TextMeshProUGUI>("text");

        private static readonly AccessTools.FieldRef<AutoPump, Item> AutoPumpItemRef =
            AccessTools.FieldRefAccess<AutoPump, Item>("item");

        private static readonly AccessTools.FieldRef<AutoPump, float> AutoPumpCooldownRef =
            AccessTools.FieldRefAccess<AutoPump, float>("coolDown");

        private static readonly AccessTools.FieldRef<AutoPump, float> AutoPumpWearCheckRef =
            AccessTools.FieldRefAccess<AutoPump, float>("wearCheckTime");

        private static readonly AccessTools.FieldRef<AutoPump, bool> AutoPumpWornRef =
            AccessTools.FieldRefAccess<AutoPump, bool>("worn");

        private static readonly Func<AutoPump, Body?> AutoPumpBodyGetter =
            AccessTools.MethodDelegate<Func<AutoPump, Body?>>(AccessTools.PropertyGetter(typeof(AutoPump), "body"));

        [HarmonyPatch(typeof(Body), "Start")]
        [HarmonyPostfix]
        private static void Body_Start_Postfix(Body __instance)
        {
            if (__instance == null) return;
            var hook = __instance.gameObject.GetComponent<CardiacWorkerLifetimeHook>();
            if (hook == null)
            {
                hook = __instance.gameObject.AddComponent<CardiacWorkerLifetimeHook>();
                hook.TargetBody = __instance;
            }
            CardiacModManager.GetOrCreate(__instance);
        }

        [HarmonyPatch(typeof(Body), nameof(Body.TryStartFibrillation))]
        [HarmonyPrefix]
        private static bool Body_TryStartFibrillation_Prefix(Body __instance)
        {
            // Strictly lock vanilla fibrillationProgress at 0; do NOT inject vanilla triggers into the mod
            __instance.fibrillationProgress = 0f;
            __instance.fibrillationForced = false;
            return false;
        }

        [HarmonyPatch(typeof(Body), nameof(Body.HandleCirculation))]
        [HarmonyPrefix]
        private static void Body_HandleCirculation_Prefix(Body __instance, Painkillers painkillers)
        {
            var ctrl = CardiacModManager.GetOrCreate(__instance);
            ctrl.PrevBloodPressure = __instance.bloodPressure;

            // Lock vanilla fibrillation value to 0
            __instance.fibrillationProgress = 0f;
            __instance.fibrillationForced = false;

            var env = CardiacModManager.BuildEnvironment(__instance, painkillers, ctrl, Time.deltaTime);
            ctrl.Worker.UpdateEnvironment(env);
            ctrl.Worker.EnqueueDeltaTime(Time.deltaTime);

            ctrl.LastSnapshot = ctrl.Worker.GetTelemetrySnapshot();

            // Sync Body.heartRate with effective mechanical pulse (0 in VF/Asystole so thump audio & arrest moodles work)
            if (!__instance.alive ||
                ctrl.LastSnapshot.Rhythm == RhythmType.VentricularFibrillation ||
                ctrl.LastSnapshot.Rhythm == RhythmType.Asystole)
            {
                __instance.heartRate = 0f;
            }
            else
            {
                __instance.heartRate = ctrl.LastSnapshot.HeartRateBpm;
            }
        }

        [HarmonyPatch(typeof(Body), nameof(Body.HandleCirculation))]
        [HarmonyPostfix]
        private static void Body_HandleCirculation_Postfix(Body __instance, Painkillers painkillers)
        {
            var ctrl = CardiacModManager.GetOrCreate(__instance);
            ref readonly var snap = ref ctrl.LastSnapshot;

            // Ensure vanilla fibrillationProgress stays locked at 0
            __instance.fibrillationProgress = 0f;
            __instance.fibrillationForced = false;

            // Keep mechanical pulse synced against vanilla's heartRate lerp/reset
            if (!__instance.alive ||
                snap.Rhythm == RhythmType.VentricularFibrillation ||
                snap.Rhythm == RhythmType.Asystole)
            {
                __instance.heartRate = 0f;
            }
            else
            {
                __instance.heartRate = snap.HeartRateBpm;
            }

            // Compute vanilla-equivalent internal bloodPressure from rolling 5s arterial MAP + vascular tone
            __instance.bloodPressure = CardiacModManager.ComputeVanillaBloodPressureFromArterial(__instance, in snap);

            // Display real rolling 5-second arterial systolic / diastolic pressure across WoundView & Defib
            int sbp = __instance.alive ? Mathf.RoundToInt(snap.SystolicBp5s) : 0;
            int dbp = __instance.alive ? Mathf.RoundToInt(snap.DiastolicBp5s) : 0;
            __instance.bloodPressureReadout = sbp.ToString() + "/" + dbp.ToString();
        }

        [HarmonyPatch(typeof(AutoPump), "Update")]
        [HarmonyPrefix]
        private static bool AutoPump_Update_Prefix(AutoPump __instance)
        {
            if (__instance == null) return false;

            ref float coolDown = ref AutoPumpCooldownRef(__instance);
            ref float wearCheckTime = ref AutoPumpWearCheckRef(__instance);
            ref bool worn = ref AutoPumpWornRef(__instance);
            Item? item = AutoPumpItemRef(__instance);
            Body? body = AutoPumpBodyGetter(__instance);

            coolDown -= Time.deltaTime;
            wearCheckTime -= Time.deltaTime;

            if (wearCheckTime < 0f)
            {
                wearCheckTime = 1f;
                worn = body != null && item != null && body.HasWearable(item);
            }

            if (!worn || item == null || item.battery == null || !item.battery.hasCharge || body == null)
                return false;

            item.battery.DrainCharge(Time.deltaTime / 1200f);

            if (body.bloodPressure < 85f && coolDown <= 0f)
            {
                coolDown = 0.3f;
                // Inject real mechanical CPR arterial compression pulse into Windkessel model instead of raw BP addition
                CardiacModManager.InjectCprPulse(body, 26f);
                item.battery.DrainCharge(0.002f);
                Sound.Play("autopump", Vector2.zero, true, true, null, 0.6f, 1f, false, false);
            }

            return false;
        }

        [HarmonyPatch(typeof(Body), nameof(Body.GetECGHeight))]
        [HarmonyPrefix]
        private static bool Body_GetECGHeight_Prefix(Body __instance, float offset, ref float __result)
        {
            if (!__instance.alive)
            {
                __result = 0f;
                return false;
            }

            if (__instance.defibShockedFrames > 0)
            {
                __result = (UnityEngine.Random.value > 0.5f) ? 1f : -1f;
                return false;
            }

            var ctrl = CardiacModManager.GetOrCreate(__instance);
            // Sample body's per-save selected frontal lead with 28 ms peak-preserving window
            float val = ctrl.Worker.SampleLeadPeakPreserving(ctrl.SelectedLead, Mathf.Max(0f, offset), 0.028f);
            __result = Mathf.Clamp(val, -1f, 1f);
            return false;
        }

        [HarmonyPatch(typeof(Body), nameof(Body.TryLastStand))]
        [HarmonyPostfix]
        private static void Body_TryLastStand_Postfix(Body __instance)
        {
            __instance.fibrillationProgress = 0f;
            __instance.fibrillationForced = false;
            if (__instance.succesfullyRolledLastStand || __instance.brainHealth > 50f)
            {
                __instance.heartRate = 120f;
                CardiacModManager.ResetToSinus(__instance, clearDrugs: false, targetBpm: 120f);
            }
        }

        [HarmonyPatch(typeof(Body), nameof(Body.Heal))]
        [HarmonyPostfix]
        private static void Body_Heal_Postfix(Body __instance)
        {
            __instance.fibrillationProgress = 0f;
            __instance.fibrillationForced = false;
            __instance.heartRate = 72f;
            __instance.bloodPressure = 120f;
            CardiacModManager.ResetToSinus(__instance, clearDrugs: true, targetBpm: 72f);
        }

        [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OnBecameUnconscious))]
        [HarmonyPostfix]
        private static void PlayerCamera_OnBecameUnconscious_Postfix(PlayerCamera __instance)
        {
            if (__instance == null) return;
            UnconsciousSpeedStates.GetOrCreateValue(__instance).ManualOverride = null;
        }

        [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OnBecameConscious))]
        [HarmonyPostfix]
        private static void PlayerCamera_OnBecameConscious_Postfix(PlayerCamera __instance)
        {
            if (__instance == null) return;
            UnconsciousSpeedStates.GetOrCreateValue(__instance).ManualOverride = null;
        }

        [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetTimeScale))]
        [HarmonyPrefix]
        private static void PlayerCamera_SetTimeScale_Prefix(
            PlayerCamera __instance,
            ref PlayerCamera.SpeedType speed,
            bool switchSound,
            bool force)
        {
            if (__instance == null || __instance.body == null) return;

            var speedState = UnconsciousSpeedStates.GetOrCreateValue(__instance);
            if (!__instance.body.conscious)
            {
                if (switchSound)
                {
                    // Player pressed speed hotkey while unconscious/dying -> allow overriding the lock
                    speedState.ManualOverride = speed;
                }
                else if (!force &&
                         (speed == PlayerCamera.SpeedType.UnconsciousFast || speed == PlayerCamera.SpeedType.DyingFast) &&
                         speedState.ManualOverride.HasValue)
                {
                    speed = speedState.ManualOverride.Value;
                }
            }
            else
            {
                speedState.ManualOverride = null;
            }
        }

        [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleInput))]
        [HarmonyPostfix]
        private static void PlayerCamera_HandleInput_Postfix(PlayerCamera __instance)
        {
            if (__instance == null || __instance.body == null) return;
            if (ConsoleScript.instance && ConsoleScript.instance.active) return;
            if (PauseHandler.paused || __instance.itemCustomizer.activeSelf || __instance.tradeMenu.activeSelf) return;
            if (__instance.craftingPanel.activeSelf && __instance.recipeFilterField != null && __instance.recipeFilterField.isFocused) return;

            KeyCode boundKey = KeyBinds.GetBind("realisticecg.multilead_hotkey");
            KeyCode activeKey = boundKey != KeyCode.None ? boundKey : CardiacModManager.MultiLeadEcgKey;
            CardiacModManager.MultiLeadEcgKey = activeKey;

            if (activeKey != KeyCode.None && Input.GetKeyDown(activeKey))
            {
                CardiacModManager.OpenOrToggleMultiLeadMinigame(__instance.body, item: null);
            }

            KeyCode boundMonitorKey = KeyBinds.GetBind("realisticecg.monitor_overlay_hotkey");
            KeyCode activeMonitorKey = boundMonitorKey != KeyCode.None ? boundMonitorKey : CardiacModManager.MonitorOverlayKey;
            CardiacModManager.MonitorOverlayKey = activeMonitorKey;

            if (activeMonitorKey != KeyCode.None && Input.GetKeyDown(activeMonitorKey))
            {
                CardiacModManager.ToggleMonitorOverlay(__instance.body);
            }
        }

        [HarmonyPatch(typeof(WoundView), nameof(WoundView.UpdateView))]
        [HarmonyPostfix]
        private static void WoundView_UpdateView_Postfix(WoundView __instance)
        {
            if (__instance == null || __instance.body == null) return;

            var ui = WoundViewLeadUis.GetOrCreateValue(__instance);
            if (!ui.Initialized)
            {
                ECGVisualizer? chipEcg = FindChipPanelEcgVisualizer(__instance);
                if (chipEcg != null)
                {
                    CreateWoundViewChipButtons(__instance, chipEcg, ui);
                    ui.Initialized = true;
                }
            }

            bool hasChip = !WorldGeneration.unchipped;
            if (ui.LeadButton != null && ui.LeadButton.gameObject.activeSelf != hasChip)
                ui.LeadButton.gameObject.SetActive(hasChip);
            if (ui.MultiLeadButton != null && ui.MultiLeadButton.gameObject.activeSelf != hasChip)
                ui.MultiLeadButton.gameObject.SetActive(hasChip);
            if (ui.MonitorOverlayButton != null && ui.MonitorOverlayButton.gameObject.activeSelf != hasChip)
                ui.MonitorOverlayButton.gameObject.SetActive(hasChip);

            if (ui.LeadLabel != null)
            {
                string leadLabel = CardiacModManager.GetFrontalLeadLabel(CardiacModManager.GetSelectedLead(__instance.body));
                ui.LeadLabel.text = $"{leadLabel} ⇄";
            }

            if (ui.MonitorOverlayBg != null)
            {
                ui.MonitorOverlayBg.color = PhysiologyMonitorOverlay.IsOpen
                    ? new Color(0.24f, 0.12f, 0.14f, 0.96f)
                    : new Color(0.14f, 0.09f, 0.10f, 0.92f);
            }

            PhysiologyMonitorOverlay.SyncWithWoundView(__instance);
        }

        private static ECGVisualizer? FindChipPanelEcgVisualizer(WoundView view)
        {
            var direct = view.GetComponentInChildren<ECGVisualizer>(true);
            if (direct != null) return direct;

            ECGVisualizer[] all = UnityEngine.Object.FindObjectsOfType<ECGVisualizer>(true);
            ECGVisualizer? comaEcg = PlayerCamera.main != null ? PlayerCamera.main.consciousnessECG : null;
            Transform? minigameRoot = MinigameBase.main != null ? MinigameBase.main.minigameScreen : null;

            foreach (var ecg in all)
            {
                if (ecg == null || ecg == comaEcg) continue;
                if (minigameRoot != null && ecg.transform.IsChildOf(minigameRoot)) continue;
                return ecg;
            }
            return null;
        }

        private static void CreateWoundViewChipButtons(WoundView view, ECGVisualizer chipEcg, WoundViewLeadUi ui)
        {
            var ecgRt = chipEcg.GetComponent<RectTransform>();
            if (ecgRt == null) return;

            bool hasMask = chipEcg.GetComponent<Mask>() != null || chipEcg.GetComponent<RectMask2D>() != null;
            Transform parent = hasMask && ecgRt.parent != null ? ecgRt.parent : ecgRt;

            Vector2 baseAnchorMin = hasMask && ecgRt.parent != null ? ecgRt.anchorMin : new Vector2(1f, 0.5f);
            Vector2 baseAnchorMax = hasMask && ecgRt.parent != null ? ecgRt.anchorMax : new Vector2(1f, 0.5f);
            float baseX = hasMask && ecgRt.parent != null
                ? ecgRt.anchoredPosition.x + (1f - ecgRt.pivot.x) * ecgRt.rect.width + 6f
                : 6f;
            float baseY = hasMask && ecgRt.parent != null
                ? ecgRt.anchoredPosition.y + (0.5f - ecgRt.pivot.y) * ecgRt.rect.height
                : 0f;

            const float btnWidth = 58f;
            const float btnHeight = 20f;
            const float verticalStep = 22f;

            // 1. Top button: Lead Switch (I / II / III / aVR / aVL / aVF)
            var leadBtn = CreateStackedChipButton(
                parent,
                "CardiacLeadSwitchBtn",
                view.bpmText?.font,
                baseAnchorMin,
                baseAnchorMax,
                new Vector2(baseX, baseY + verticalStep),
                new Vector2(btnWidth, btnHeight),
                new Color(0.08f, 0.16f, 0.14f, 0.92f),
                new Color(0.42f, 1f, 0.68f, 1f),
                "II ⇄",
                12f,
                "切换心电导联",
                "点击循环切换额面导联 (I / II / III / aVR / aVL / aVF)，并与除颤仪及昏迷监控同步",
                out var leadTmp,
                out _);

            leadBtn.onClick.AddListener(() =>
            {
                Body? targetBody = view.body != null ? view.body : (PlayerCamera.main != null ? PlayerCamera.main.body : null);
                if (targetBody == null) return;
                FrontalLead next = CardiacModManager.CycleSelectedLead(targetBody, +1);
                leadTmp.text = $"{CardiacModManager.GetFrontalLeadLabel(next)} ⇄";
                if (PlayerCamera.main != null)
                    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, 1.1f);
            });

            // 2. Middle button: Directly open/toggle 6-lead ECG minigame
            var multiLeadBtn = CreateStackedChipButton(
                parent,
                "CardiacMultiLeadBtn",
                view.bpmText?.font,
                baseAnchorMin,
                baseAnchorMax,
                new Vector2(baseX, baseY),
                new Vector2(btnWidth, btnHeight),
                new Color(0.08f, 0.14f, 0.20f, 0.92f),
                new Color(0.55f, 0.92f, 1f, 1f),
                "6导联",
                11.5f,
                "打开六导联心电图",
                "直接打开标准网格六导联额面心电图检测界面（可调走纸速度与增益）",
                out _,
                out _);

            multiLeadBtn.onClick.AddListener(() =>
            {
                Body? targetBody = view.body != null ? view.body : (PlayerCamera.main != null ? PlayerCamera.main.body : null);
                if (targetBody == null) return;
                CardiacModManager.OpenOrToggleMultiLeadMinigame(targetBody, item: null);
                if (PlayerCamera.main != null)
                    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, 1.05f);
            });

            // 3. Bottom button: Open/toggle draggable real-time arterial pressure & multi-curve monitor overlay
            var monitorBtn = CreateStackedChipButton(
                parent,
                "CardiacMonitorOverlayBtn",
                view.bpmText?.font,
                baseAnchorMin,
                baseAnchorMax,
                new Vector2(baseX, baseY - verticalStep),
                new Vector2(btnWidth, btnHeight),
                new Color(0.14f, 0.09f, 0.10f, 0.92f),
                new Color(1f, 0.56f, 0.56f, 1f),
                "动脉压",
                11.5f,
                "实时生理监护曲线",
                "开关可拖动实时动脉血压波形与多曲线生理监护面板（关闭医疗面板时自动隐藏）",
                out _,
                out var monitorBg);

            monitorBtn.onClick.AddListener(() =>
            {
                Body? targetBody = view.body != null ? view.body : (PlayerCamera.main != null ? PlayerCamera.main.body : null);
                if (targetBody == null) return;
                CardiacModManager.ToggleMonitorOverlay(targetBody);
            });

            ui.LeadButton = leadBtn;
            ui.LeadLabel = leadTmp;
            ui.MultiLeadButton = multiLeadBtn;
            ui.MonitorOverlayButton = monitorBtn;
            ui.MonitorOverlayBg = monitorBg;
        }

        private static Button CreateStackedChipButton(
            Transform parent,
            string name,
            TMP_FontAsset? font,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 anchoredPos,
            Vector2 size,
            Color bgColor,
            Color textColor,
            string initialText,
            float fontSize,
            string tipName,
            string tipDesc,
            out TextMeshProUGUI labelOut,
            out Image bgOut)
        {
            var btnGo = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(parent, false);

            var rt = btnGo.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPos;

            bgOut = btnGo.GetComponent<Image>();
            bgOut.color = bgColor;

            var txtGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(btnGo.transform, false);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.sizeDelta = Vector2.zero;

            labelOut = txtGo.GetComponent<TextMeshProUGUI>();
            if (font != null) labelOut.font = font;
            labelOut.fontSize = fontSize;
            labelOut.alignment = TextAlignmentOptions.Center;
            labelOut.color = textColor;
            labelOut.text = initialText;
            labelOut.raycastTarget = false;

            var tooltip = btnGo.AddComponent<UITooltip>();
            tooltip.skipLocale = true;
            tooltip.tipName = tipName;
            tooltip.tipDesc = tipDesc;

            return btnGo.GetComponent<Button>();
        }

        [HarmonyPatch(typeof(ManualDefibMinigame), nameof(ManualDefibMinigame.Start))]
        [HarmonyPostfix]
        private static void ManualDefib_Start_Postfix(ManualDefibMinigame __instance)
        {
            HideManualDefibRecommendedRange(__instance);
            EnsureManualDefibLeadButton(__instance);
        }

        [HarmonyPatch(typeof(ManualDefibMinigame), nameof(ManualDefibMinigame.Update))]
        [HarmonyPrefix]
        private static void ManualDefib_Update_Prefix(ManualDefibMinigame __instance, List<RaycastResult> uiCasts)
        {
            CardiacModManager.PendingShockEnergyJoules = ManualChargeRef(__instance);

            if (ManualDefibLeadUis.TryGetValue(__instance, out var leadUi) && leadUi.ButtonRect != null)
            {
                Body? body = __instance.limb != null ? __instance.limb.body : null;
                if (Minigame.game != null && Minigame.game.handStartedClicking && uiCasts != null)
                {
                    foreach (RaycastResult hit in uiCasts)
                    {
                        if (hit.gameObject == leadUi.ButtonRect.gameObject || hit.gameObject.transform.IsChildOf(leadUi.ButtonRect))
                        {
                            if (body != null)
                            {
                                CardiacModManager.CycleSelectedLead(body, +1);
                            }
                            break;
                        }
                    }
                }
                if (leadUi.Label != null)
                {
                    leadUi.Label.text = "导联:" + CardiacModManager.GetFrontalLeadLabel(CardiacModManager.GetSelectedLead(body));
                }
            }
        }

        private static void EnsureManualDefibLeadButton(ManualDefibMinigame defib)
        {
            ECGVisualizer? defibEcg = ManualEcgRef(defib);
            if (defibEcg == null) return;

            var leadUi = ManualDefibLeadUis.GetOrCreateValue(defib);
            if (leadUi.ButtonRect != null) return;

            Transform parent = defibEcg.transform.parent != null ? defibEcg.transform.parent : defibEcg.transform;
            var btnGo = new GameObject("ManualDefibLeadSwitchBtn", typeof(RectTransform), typeof(Image));
            btnGo.transform.SetParent(parent, false);

            var ecgRt = defibEcg.GetComponent<RectTransform>();
            var rt = btnGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(68f, 20f);
            if (ecgRt != null && parent == defibEcg.transform.parent)
            {
                rt.anchorMin = ecgRt.anchorMin;
                rt.anchorMax = ecgRt.anchorMax;
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(
                    ecgRt.anchoredPosition.x + (1f - ecgRt.pivot.x) * ecgRt.rect.width - 3f,
                    ecgRt.anchoredPosition.y + (1f - ecgRt.pivot.y) * ecgRt.rect.height - 3f);
            }
            else
            {
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-3f, -3f);
            }

            var img = btnGo.GetComponent<Image>();
            img.color = new Color(0.08f, 0.18f, 0.14f, 0.90f);
            img.raycastTarget = true;

            var txtGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(btnGo.transform, false);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.sizeDelta = Vector2.zero;

            var tmp = txtGo.GetComponent<TextMeshProUGUI>();
            var texts = ManualTextsRef(defib);
            if (texts != null && texts.Count > 0 && texts[0] != null)
            {
                tmp.font = texts[0].font;
            }
            tmp.fontSize = 11.5f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.45f, 1f, 0.70f, 1f);
            tmp.raycastTarget = false;

            Body? body = defib.limb != null ? defib.limb.body : null;
            tmp.text = "导联:" + CardiacModManager.GetFrontalLeadLabel(CardiacModManager.GetSelectedLead(body));

            leadUi.ButtonRect = rt;
            leadUi.Label = tmp;
        }

        [HarmonyPatch(typeof(ManualDefibMinigame), nameof(ManualDefibMinigame.Update))]
        [HarmonyPostfix]
        private static void ManualDefib_Update_Postfix(ManualDefibMinigame __instance)
        {
            CardiacModManager.PendingShockEnergyJoules = null;
            HideManualDefibRecommendedRange(__instance);
        }

        private static void HideManualDefibRecommendedRange(ManualDefibMinigame defib)
        {
            var texts = ManualTextsRef(defib);
            if (texts != null && texts.Count > 4 && texts[4] != null)
            {
                texts[4].text = string.Empty;
                if (texts[4].gameObject.activeSelf)
                    texts[4].gameObject.SetActive(false);
            }
        }

        [HarmonyPatch(typeof(ECGVisualizer), "Start")]
        [HarmonyPrefix]
        private static bool ECGVisualizer_Start_Prefix(ECGVisualizer __instance)
        {
            int srcW = Mathf.Max(32, __instance.textureSize.x);
            int srcH = Mathf.Max(24, __instance.textureSize.y);

            // 3x supersampled resolution for crisp P-QRS-T and ST morphology across all 3 ECG displays
            int w = Mathf.Clamp(srcW * 3, 270, 540);
            int h = Mathf.Clamp(srcH * 3, 108, 210);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var state = EcgStates.GetOrCreateValue(__instance);
            state.Tex = tex;
            state.Width = w;
            state.Height = h;
            state.Buffer = new Color32[w * h];
            state.WriteX = 0;
            state.LastY = Mathf.RoundToInt(h * 0.42f);
            state.TimeAccum = 0f;
            state.FadeAccum = 1f;
            state.ComaStripCount = 0;
            Body? body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            state.LastLeadVersion = body != null ? CardiacModManager.GetLeadVersion(body) : -1;
            state.Img = __instance.GetComponent<RawImage>();

            if (state.Img != null)
            {
                state.Img.texture = tex;
            }

            tex.SetPixels32(state.Buffer);
            tex.Apply(false);
            return false;
        }

        [HarmonyPatch(typeof(ECGVisualizer), "Update")]
        [HarmonyPrefix]
        private static bool ECGVisualizer_Update_Prefix(ECGVisualizer __instance)
        {
            if (!__instance.active)
                return false;

            if (!EcgStates.TryGetValue(__instance, out var state) || state.Tex == null || state.Width <= 0 || state.Height <= 0)
            {
                ECGVisualizer_Start_Prefix(__instance);
                if (!EcgStates.TryGetValue(__instance, out state) || state.Tex == null)
                    return false;
            }

            Body? body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            int w = state.Width;
            int h = state.Height;
            Color32[] buf = state.Buffer;

            // Switching leads clears historical ECG buffer so different leads never mix on screen
            int currentLeadVer = body != null ? CardiacModManager.GetLeadVersion(body) : 0;
            if (state.LastLeadVersion != currentLeadVer)
            {
                state.LastLeadVersion = currentLeadVer;
                Array.Clear(buf, 0, buf.Length);
                state.WriteX = 0;
                state.LastY = Mathf.RoundToInt(h * 0.42f);
                state.TimeAccum = 0f;
                state.FadeAccum = 1f;
                state.ComaStripCount = 0;
                state.Tex.SetPixels32(buf);
                state.Tex.Apply(false);
            }

            bool highSpeedMode = Time.timeScale > 6.0f;
            float dt = highSpeedMode
                ? Mathf.Min(Time.unscaledDeltaTime * 3.0f, 0.20f)
                : Time.deltaTime;
            if (dt <= 0f)
                return false;

            if (!highSpeedMode)
            {
                state.ComaStripCount = 0;
            }

            const float sweepSpanGameSec = 2.6f; // Total game-time seconds shown across full monitor width
            state.TimeAccum = Mathf.Min(state.TimeAccum + dt, sweepSpanGameSec);

            float colDt = sweepSpanGameSec / state.Width; // ~138-180 Hz column sampling
            if (state.TimeAccum < colDt)
                return false;

            // FPS-independent phosphor decay (~0.62^2.6 = 0.29 remaining across full 2.6s screen width -> trace stays visible up to eraser gap)
            state.FadeAccum *= Mathf.Pow(0.62f, Mathf.Min(dt, 0.8f));
            if (state.FadeAccum <= 0.97f)
            {
                int fadeScale = Mathf.Clamp(Mathf.RoundToInt(state.FadeAccum * 256f), 100, 252);
                state.FadeAccum = 1f;
                for (int p = 0; p < buf.Length; p++)
                {
                    byte a = buf[p].a;
                    if (a > 0)
                    {
                        buf[p].a = (byte)((a * fadeScale) >> 8);
                    }
                }
            }

            const int eraserGapCols = 7;
            while (state.TimeAccum >= colDt)
            {
                state.TimeAccum -= colDt;
                state.WriteX++;
                if (state.WriteX >= w)
                    state.WriteX = 0;

                if (highSpeedMode && __instance.followBody && body != null && (state.WriteX == 0 || state.ComaStripCount < 2))
                {
                    var ctrl = CardiacModManager.GetOrCreate(body);
                    int want = Mathf.Min(1300, state.ComaStripBuffer.Length);
                    state.ComaStripCount = ctrl.Worker.CopyRecentSamplesChronological(state.ComaStripBuffer, want);
                }

                // Clear sweep-bar gap ahead of beam so new trace never overlaps stale sweep
                for (int g = 1; g <= eraserGapCols; g++)
                {
                    int clearX = (state.WriteX + g) % w;
                    for (int cy = 0; cy < h; cy++)
                    {
                        buf[cy * w + clearX].a = 0;
                    }
                }

                float sample;
                if (__instance.followBody && body != null)
                {
                    if (highSpeedMode && state.ComaStripCount >= 2)
                    {
                        sample = SampleComaStripColumn(body, state, state.WriteX, w);
                    }
                    else
                    {
                        sample = CardiacModManager.SampleLeadForEcgVisualizer(body, state.TimeAccum, colDt);
                    }
                    __instance.writeHeight = sample;
                }
                else
                {
                    sample = __instance.writeHeight;
                }

                // Place baseline at 42% height so upright R-peaks & ST elevation have generous headroom
                int y = Mathf.Clamp(Mathf.RoundToInt((0.42f + sample * 0.46f) * (h - 1)), 1, h - 2);
                int yMin = Mathf.Min(state.LastY, y);
                int yMax = Mathf.Max(state.LastY, y);

                int wx = state.WriteX;
                for (int py = yMin; py <= yMax; py++)
                {
                    buf[py * w + wx] = new Color32(255, 255, 255, 255);
                }

                // Crisp 1px vertical anti-aliased edge
                if (yMin - 1 >= 0)
                {
                    int idx = (yMin - 1) * w + wx;
                    buf[idx] = new Color32(255, 255, 255, (byte)Mathf.Max(115, buf[idx].a));
                }
                if (yMax + 1 < h)
                {
                    int idx = (yMax + 1) * w + wx;
                    buf[idx] = new Color32(255, 255, 255, (byte)Mathf.Max(115, buf[idx].a));
                }

                state.LastY = y;
            }

            state.Tex.SetPixels32(buf);
            state.Tex.Apply(false);
            return false;
        }

        private static float SampleComaStripColumn(Body body, EcgVisualizerRenderState state, int wx, int width)
        {
            if (body == null || !body.alive) return 0f;
            if (body.defibShockedFrames > 0)
                return (UnityEngine.Random.value > 0.5f) ? 1f : -1f;

            int count = state.ComaStripCount;
            if (count <= 0) return 0f;

            FrontalLead lead = CardiacModManager.GetSelectedLead(body);
            float u0 = wx / (float)Mathf.Max(1, width);
            float u1 = (wx + 1) / (float)Mathf.Max(1, width);
            int idx0 = Mathf.Clamp(Mathf.FloorToInt(u0 * (count - 1)), 0, count - 1);
            int idx1 = Mathf.Clamp(Mathf.CeilToInt(u1 * (count - 1)), 0, count - 1);

            float best = 0f;
            float bestAbs = -1f;
            for (int k = idx0; k <= idx1; k++)
            {
                float v = state.ComaStripBuffer[k].GetLead(lead);
                float av = Math.Abs(v);
                if (av > bestAbs)
                {
                    bestAbs = av;
                    best = v;
                }
            }
            return Mathf.Clamp(best, -1f, 1f);
        }

        [HarmonyPatch(typeof(AEDMinigame), nameof(AEDMinigame.Update))]
        [HarmonyPrefix]
        private static bool AEDMinigame_Update_Prefix(AEDMinigame __instance, List<RaycastResult> uiCasts)
        {
            if (!Minigame.game.currentItem.battery.hasCharge)
                return true;

            ref int state = ref AedStateRef(__instance);
            ref float timer = ref AedTimerRef(__instance);
            TextMeshProUGUI text = AedTextRef(__instance);

            if (state == 1)
            {
                timer += Time.deltaTime;
                if (timer > 4.25f)
                {
                    // Check real grid VF state on torso pads
                    bool padsOnTorso = __instance.limb != null && __instance.limb.body != null && __instance.limb == __instance.limb.body.limbs[1];
                    bool isVf = padsOnTorso && CardiacModManager.IsVentricularFibrillation(__instance.limb!.body);

                    if (isVf)
                    {
                        state = 2;
                        Sound.Play("aedstart", Vector2.zero, true, false, null, 1f, 1f, false, false);
                    }
                    else
                    {
                        state = 5;
                        Sound.Play("aedfail", Vector2.zero, true, false, null, 1f, 1f, false, false);
                    }

                    Minigame.game.currentItem.battery.DrainCharge(0.02f);
                    timer = 0f;
                    Minigame.game.UpdateHandSprite(true);
                }

                if (text != null)
                {
                    text.text = Locale.GetOther("aedstate1") + "...".Substring(2 - (int)(Time.time * 3f) % 3);
                }
                return false;
            }

            if (state == 3 && timer + Time.deltaTime > 3.5f)
            {
                // AED delivers fixed 150 J biphasic shock
                CardiacModManager.PendingShockEnergyJoules = 150f;
            }

            return true;
        }

        [HarmonyPatch(typeof(AEDMinigame), nameof(AEDMinigame.Update))]
        [HarmonyPostfix]
        private static void AEDMinigame_Update_Postfix()
        {
            CardiacModManager.PendingShockEnergyJoules = null;
        }

        [HarmonyPatch(typeof(Item), nameof(Item.Defibrillate))]
        [HarmonyPrefix]
        private static bool Item_Defibrillate_Prefix(Item.DefibInfo info)
        {
            if (info == null || info.limb == null)
                return false;

            info.limb.skinHealth -= 5f;
            info.limb.pain += 20f;
            info.limb.body.Ragdoll();

            if (info.limb == info.limb.body.limbs[1] && info.limb.body.alive)
            {
                float energyJoules = CardiacModManager.PendingShockEnergyJoules ?? 150f;
                CardiacModManager.Defibrillate(info.limb.body, energyJoules);

                info.limb.body.fibrillationProgress = 0f;
                info.limb.body.defibShockedFrames = 20;
                info.limb.body.heartProg = -1f;
            }

            return false;
        }
    }
}
