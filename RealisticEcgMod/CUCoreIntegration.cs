using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CardiacSimCore;
using CUCoreLib.Data;
using CUCoreLib.Registries;
using CUCoreLib.Saving;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RealisticEcgMod
{
    internal sealed class CardiacBodySaveProvider : IBodySaveProvider
    {
        public int GetVersion() => 1;

        public JToken Capture(Body body)
        {
            var ctrl = CardiacModManager.GetOrCreate(body);
            ctrl.LastSnapshot = ctrl.Worker.GetTelemetrySnapshot();
            ref readonly var snap = ref ctrl.LastSnapshot;

            return new JObject
            {
                ["lead"] = (int)ctrl.SelectedLead,
                ["exoAdren"] = ctrl.ExogenousAdrenaline,
                ["exoDepot"] = ctrl.ExogenousAdrenalineDepot,
                ["isch"] = ctrl.MyocardialIschemiaLevel,
                ["amioConc"] = snap.AmiodaroneConcentration,
                ["amioPool"] = snap.AmiodaronePendingPool,
                ["lidoConc"] = snap.LidocaineConcentration,
                ["lidoPool"] = snap.LidocainePendingPool,
                ["atp"] = snap.MyocardialAtpReserve,
                ["rhythm"] = (int)snap.Rhythm,
                ["hr"] = snap.HeartRateBpm
            };
        }

        public void Restore(Body body, JToken payload, int version, SaveRestoreContext context)
        {
            if (body == null || payload is not JObject obj) return;

            var ctrl = CardiacModManager.GetOrCreate(body);
            if (obj.TryGetValue("lead", out var leadTok))
            {
                int leadVal = leadTok.Value<int>();
                if (Enum.IsDefined(typeof(FrontalLead), leadVal))
                {
                    CardiacModManager.SetSelectedLead(body, (FrontalLead)leadVal);
                }
            }

            ctrl.ExogenousAdrenaline = Mathf.Max(0f, obj["exoAdren"]?.Value<float>() ?? 0f);
            ctrl.ExogenousAdrenalineDepot = Mathf.Max(0f, obj["exoDepot"]?.Value<float>() ?? 0f);
            ctrl.MyocardialIschemiaLevel = Mathf.Clamp01(obj["isch"]?.Value<float>() ?? 0f);
            ctrl.PrevBloodPressure = body.bloodPressure;

            float amioConc = obj["amioConc"]?.Value<float>() ?? 0f;
            float amioPool = obj["amioPool"]?.Value<float>() ?? 0f;
            float lidoConc = obj["lidoConc"]?.Value<float>() ?? 0f;
            float lidoPool = obj["lidoPool"]?.Value<float>() ?? 0f;
            float atp = obj["atp"]?.Value<float>() ?? 1f;
            int rhythmInt = obj["rhythm"]?.Value<int>() ?? (int)RhythmType.NormalSinus;
            RhythmType rhythm = Enum.IsDefined(typeof(RhythmType), rhythmInt)
                ? (RhythmType)rhythmInt
                : RhythmType.NormalSinus;
            float hr = obj["hr"]?.Value<float>() ?? 72f;

            ctrl.Worker.RestoreSavedStateImmediate(amioConc, amioPool, lidoConc, lidoPool, atp, rhythm, hr);
        }
    }

    internal static class CUCoreIntegration
    {
        public static void RegisterAll(ManualLogSource logger)
        {
            RegisterLocales();
            RegisterModOptions();
            RegisterSaveProviders();
            RegisterLidocaineAndRecipe();
            RegisterEcgMonitorItem();
            logger.LogInfo("CUCoreLib integrations registered (lidocaine, recipe, ecgmonitor, settings keybind, per-save persistence).");
        }

        private static void RegisterLocales()
        {
            LocaleRegistry.Register(LocaleRegistry.LocaleCategory.Other, "multileadEcgGuide",
                "六导联心电图仪 — 左键调节走纸速度、增益或冻结波形测量");
        }

        private static void RegisterModOptions()
        {
            ModOptionsRegistry.Register(ModOptionDefinition.Keybind(
                id: "realisticecg.multilead_hotkey",
                label: "多导联心电图面板热键",
                description: "在持有生物芯片时开关六导联标准网格心电图检测界面。",
                category: Setting.SettingCategory.Input,
                defaultValue: KeyCode.G,
                apply: key =>
                {
                    CardiacModManager.MultiLeadEcgKey = key;
                }));
        }

        private static void RegisterSaveProviders()
        {
            SaveRegistry.RegisterBodyProvider("realisticecg.cardiacState", new CardiacBodySaveProvider());
        }

        private static void RegisterLidocaineAndRecipe()
        {
            LiquidRegistry.Register("lidocaine", new CustomLiquidInfo
            {
                name = "利多卡因",
                description = "Ib类抗心律失常药。静脉注射可抑制缺血或儿茶酚胺过载引起的室性早搏与室速（每10mL含100mg，半衰期约2分钟）。",
                color = new Color(0.74f, 0.94f, 1.0f, 0.94f),
                valuePerLiter = 200f,
                healthUsable = true,
                injectable = true,
                injectionSickness = 0f,
                onHealthUse = (ml, limb) =>
                {
                    if (limb?.body == null) return;
                    CardiacModManager.AdministerLidocaine(limb.body, ml);
                },
                onInject = (ml, limb) =>
                {
                    if (limb?.body == null) return;
                    CardiacModManager.AdministerLidocaine(limb.body, ml);
                },
                onDrink = (ml, body) =>
                {
                    // Oral lidocaine has high first-pass hepatic metabolism (~15% systemic bioavailability)
                    if (body == null) return;
                    CardiacModManager.AdministerLidocaine(body, ml * 0.15f);
                }
            });

            RecipeRegistry.Register(new Recipe
            {
                INT = 8,
                category = Recipes.RecipeCategory.Medicine,
                result = new RecipeResult
                {
                    id = "lidocaine",
                    isLiquid = true,
                    resultCondition = 25f
                },
                items = new List<RecipeItem>
                {
                    new RecipeItem(50f)
                    {
                        specificId = "localanesthetic",
                        specific = true,
                        isLiquid = true
                    },
                    new RecipeItem(10f)
                    {
                        specificId = "biochem",
                        specific = true,
                        isLiquid = true
                    }
                }
            });
        }

        private static void RegisterEcgMonitorItem()
        {
            Sprite monitorIcon = CreateEcgMonitorSprite();

            ItemRegistry.Register("ecgmonitor", new CustomItemInfo
            {
                fullName = "便携式心电图仪",
                description = "便携式六导联额面心电图监护仪，可在无生物芯片的情况下打开标准网格心电图界面。",
                category = "medical",
                tags = "medicine",
                weight = 0.1f,
                value = 45,
                usable = true,
                usableWithLMB = true,
                usableOnLimb = true,
                destroyAtZeroCondition = false,
                onlyHoldInHands = false,
                DropPool = DropPool.MedicalCrate,
                SpawnFrequency = 1,
                Icon = monitorIcon,
                useAction = (body, item) =>
                {
                    if (body != null)
                    {
                        CardiacModManager.OpenOrToggleMultiLeadMinigame(body, item);
                    }
                },
                useLimbAction = (limb, item) =>
                {
                    if (limb?.body != null)
                    {
                        CardiacModManager.OpenOrToggleMultiLeadMinigame(limb.body, item);
                    }
                }
            });
        }

        private static Sprite CreateEcgMonitorSprite()
        {
            const int size = 24;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            var clear = new Color32(0, 0, 0, 0);
            var casing = new Color32(205, 214, 222, 255);
            var edgeDark = new Color32(72, 86, 98, 255);
            var screenBg = new Color32(14, 34, 24, 255);
            var waveGreen = new Color32(85, 255, 138, 255);
            var btnRed = new Color32(220, 72, 72, 255);
            var btnCyan = new Color32(85, 200, 235, 255);

            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Color32 c = clear;
                    bool inBody = x >= 3 && x <= 20 && y >= 2 && y <= 19;
                    bool inHandle = x >= 8 && x <= 15 && y >= 20 && y <= 22 && !(y == 20 && x >= 10 && x <= 13);
                    if (inHandle)
                    {
                        c = edgeDark;
                    }
                    else if (inBody)
                    {
                        bool border = x == 3 || x == 20 || y == 2 || y == 19;
                        bool inScreen = x >= 5 && x <= 18 && y >= 8 && y <= 17;
                        if (border) c = edgeDark;
                        else if (inScreen) c = screenBg;
                        else c = casing;
                    }
                    px[y * size + x] = c;
                }
            }

            // Draw crisp ECG complex inside tiny screen
            int[] wx = { 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 };
            int[] wy = { 11, 12, 11, 10, 16, 9, 11, 11, 13, 12, 11, 11 };
            for (int k = 0; k < wx.Length; k++)
            {
                px[wy[k] * size + wx[k]] = waveGreen;
            }
            px[13 * size + 10] = waveGreen;
            px[14 * size + 10] = waveGreen;
            px[15 * size + 10] = waveGreen;

            // Front buttons
            px[5 * size + 6] = btnRed;
            px[5 * size + 7] = btnRed;
            px[5 * size + 10] = btnCyan;
            px[5 * size + 11] = btnCyan;
            px[5 * size + 15] = edgeDark;
            px[5 * size + 16] = edgeDark;

            tex.SetPixels32(px);
            tex.Apply(false);

            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 24f);
        }
    }
}
