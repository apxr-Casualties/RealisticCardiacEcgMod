using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CardiacSimCore;
using UnityEngine;

namespace RealisticEcgMod
{
    /// <summary>
    /// Per-Body simulation state wrapping a dedicated background CardiacWorkerRunner.
    /// </summary>
    public sealed class BodyCardiacController : IDisposable
    {
        public readonly CardiacWorkerRunner Worker;
        public CardiacTelemetrySnapshot LastSnapshot;
        public float PrevBloodPressure = 120f;
        public float MyocardialIschemiaLevel;
        public float ExogenousAdrenaline;
        public float ExogenousAdrenalineDepot;
        public FrontalLead SelectedLead = CardiacModManager.DefaultLead;
        public int LeadVersion;
        private bool _disposed;

        public float GetTotalAdrenaline(Body body) => Mathf.Max(0f, body.curAdrenaline) + ExogenousAdrenaline;

        public BodyCardiacController(Body body)
        {
            var sim = new CardiacGridSimulator(body.GetInstanceID());
            Worker = new CardiacWorkerRunner(sim);
            LastSnapshot = Worker.GetTelemetrySnapshot();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Worker.Dispose();
        }
    }

    /// <summary>
    /// Central coordinator bridging Casualties Unknown Body instances to off-thread cardiac simulators.
    /// Provides hooks for ECG lead readout, blood pressure via LV ejection, amiodarone dosing, and defibrillation.
    /// </summary>
    public static class CardiacModManager
    {
        private const float NormalLvCardiacOutputLpm = 5.78f;

        private static readonly ConditionalWeakTable<Body, BodyCardiacController> Controllers =
            new ConditionalWeakTable<Body, BodyCardiacController>();

        private static readonly List<WeakReference<BodyCardiacController>> ActiveRefs =
            new List<WeakReference<BodyCardiacController>>();

        private static readonly object RegistryLock = new object();

        public static FrontalLead DefaultLead { get; set; } = FrontalLead.II;
        public static KeyCode MultiLeadEcgKey { get; set; } = KeyCode.G;

        private static readonly FrontalLead[] AllFrontalLeads =
        {
            FrontalLead.I,
            FrontalLead.II,
            FrontalLead.III,
            FrontalLead.aVR,
            FrontalLead.aVL,
            FrontalLead.aVF
        };

        // Thread-static / main-thread pending shock energy context for ManualDefibMinigame & AEDMinigame
        public static float? PendingShockEnergyJoules;

        public static FrontalLead GetSelectedLead(Body? body)
        {
            if (body == null) return DefaultLead;
            return GetOrCreate(body).SelectedLead;
        }

        public static int GetLeadVersion(Body? body)
        {
            if (body == null) return 0;
            return GetOrCreate(body).LeadVersion;
        }

        public static void SetSelectedLead(Body? body, FrontalLead lead)
        {
            if (body == null)
            {
                DefaultLead = lead;
                return;
            }
            var ctrl = GetOrCreate(body);
            if (ctrl.SelectedLead != lead)
            {
                ctrl.SelectedLead = lead;
                ctrl.LeadVersion++;
            }
        }

        public static FrontalLead CycleSelectedLead(Body? body, int delta = 1)
        {
            FrontalLead current = GetSelectedLead(body);
            int idx = Array.IndexOf(AllFrontalLeads, current);
            if (idx < 0) idx = 1;
            int nextIdx = (idx + delta % AllFrontalLeads.Length + AllFrontalLeads.Length) % AllFrontalLeads.Length;
            FrontalLead next = AllFrontalLeads[nextIdx];
            SetSelectedLead(body, next);
            return next;
        }

        public static string GetFrontalLeadLabel(FrontalLead lead) => lead switch
        {
            FrontalLead.I => "I",
            FrontalLead.II => "II",
            FrontalLead.III => "III",
            FrontalLead.aVR => "aVR",
            FrontalLead.aVL => "aVL",
            FrontalLead.aVF => "aVF",
            _ => "II"
        };

        public static string GetRhythmDisplayName(in CardiacTelemetrySnapshot snap)
        {
            switch (snap.Rhythm)
            {
                case RhythmType.Asystole:
                    return "心室停搏 (Asystole)";
                case RhythmType.VentricularFibrillation:
                    return "心室颤动 (VF)";
                case RhythmType.VentricularTachycardia:
                    return "室性心动过速 (VT)";
                case RhythmType.SinusTachycardia:
                    return snap.HasRecentPvc ? "窦性心动过速伴室早" : "窦性心动过速";
                case RhythmType.SinusBradycardia:
                    return snap.HasRecentPvc ? "窦性心动过缓伴室早" : "窦性心动过缓";
                case RhythmType.NormalSinus:
                default:
                    return snap.HasRecentPvc ? "窦性心律伴室早" : "正常窦性心律";
            }
        }

        public static BodyCardiacController GetOrCreate(Body body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (Controllers.TryGetValue(body, out var existing))
                return existing;

            lock (RegistryLock)
            {
                if (Controllers.TryGetValue(body, out existing))
                    return existing;

                var created = new BodyCardiacController(body);
                Controllers.Add(body, created);
                ActiveRefs.Add(new WeakReference<BodyCardiacController>(created));
                return created;
            }
        }

        public static void DisposeBody(Body body)
        {
            if (body == null) return;
            if (Controllers.TryGetValue(body, out var ctrl))
            {
                ctrl.Dispose();
            }
        }

        public static void DisposeAll()
        {
            lock (RegistryLock)
            {
                foreach (var wr in ActiveRefs)
                {
                    if (wr.TryGetTarget(out var ctrl))
                        ctrl.Dispose();
                }
                ActiveRefs.Clear();
            }
        }

        public static CardiacEnvironmentInput BuildEnvironment(Body body, Painkillers? painkillers, BodyCardiacController ctrl, float deltaTime)
        {
            float targetBpm = 0f;
            if (body.alive)
            {
                float positiveViscosity = Mathf.Max(0f, body.bloodViscosity);
                float bpm = 70f;
                bpm += body.averagePain;
                bpm += (100f - body.stamina) * 0.6f;
                bpm += body.curAdrenaline * 0.35f;
                bpm -= positiveViscosity * 0.3f;
                bpm += body.tempDiffFromNormal * 0.5f;
                if (painkillers != null)
                {
                    bpm -= painkillers.actualOpiateReception / 5f;
                }
                bpm += body.heartRatePressureOffset;
                targetBpm = Mathf.Clamp(bpm, 25f, 210f);
            }

            // Update uncapped exogenous adrenaline pharmacokinetics (absorption from IV depot + clearance)
            if (deltaTime > 0f)
            {
                if (ctrl.ExogenousAdrenalineDepot > 0f)
                {
                    float absorbed = Mathf.Min(
                        ctrl.ExogenousAdrenalineDepot,
                        Mathf.Max(35f, ctrl.ExogenousAdrenalineDepot * 1.6f) * deltaTime);
                    ctrl.ExogenousAdrenalineDepot -= absorbed;
                    ctrl.ExogenousAdrenaline += absorbed;
                }
                if (ctrl.ExogenousAdrenaline > 0f)
                {
                    float clearance = (1.6f + 0.014f * ctrl.ExogenousAdrenaline) * deltaTime;
                    ctrl.ExogenousAdrenaline = Mathf.Max(0f, ctrl.ExogenousAdrenaline - clearance);
                }
            }

            float totalAdrenaline = ctrl.GetTotalAdrenaline(body);
            float catecholStress = totalAdrenaline > 110f ? Mathf.Clamp01((totalAdrenaline - 110f) / 220f) : 0f;

            // Smooth buffered myocardial hypoxia/ischemia & catecholamine calcium overload stress
            float oxyStress = body.bloodOxygen < 85f ? Mathf.Clamp01((85f - body.bloodOxygen) / 45f) : 0f;
            float bpStress = body.bloodPressure < 85f ? Mathf.Clamp01((85f - body.bloodPressure) / 55f) : 0f;
            float hypoStress = Mathf.Max(oxyStress, bpStress) + 0.25f * Mathf.Min(oxyStress, bpStress);
            float targetIschemia = Mathf.Clamp01(Mathf.Max(hypoStress, catecholStress * 0.85f) + 0.20f * Mathf.Min(hypoStress, catecholStress));

            if (deltaTime > 0f)
            {
                if (targetIschemia > ctrl.MyocardialIschemiaLevel)
                {
                    float riseRate = 0.015f + 0.030f * (targetIschemia - ctrl.MyocardialIschemiaLevel);
                    ctrl.MyocardialIschemiaLevel = Mathf.MoveTowards(ctrl.MyocardialIschemiaLevel, targetIschemia, deltaTime * riseRate);
                }
                else
                {
                    const float fallRate = 0.020f;
                    ctrl.MyocardialIschemiaLevel = Mathf.MoveTowards(ctrl.MyocardialIschemiaLevel, targetIschemia, deltaTime * fallRate);
                }
            }

            float isch = ctrl.MyocardialIschemiaLevel;
            float subendoIsch = isch > 0.10f ? Mathf.Clamp01((isch - 0.10f) / 0.45f) : 0f;
            float focalTransmural = isch > 0.38f ? Mathf.Clamp01((isch - 0.38f) / 0.55f) : 0f;
            float globalIsch = isch > 0.30f ? Mathf.Clamp01((isch - 0.30f) / 0.65f) : 0f;

            return new CardiacEnvironmentInput
            {
                TargetSinusBpm = targetBpm,
                GlobalIschemia = globalIsch,
                FocalTransmuralIschemia = focalTransmural,
                SubendocardialIschemia = subendoIsch,
                RightVentricularIschemia = 0f,
                PreloadFactor = Mathf.Clamp(body.bloodVolumePercentage, 0.15f, 1.35f),
                TemperatureC = Mathf.Clamp(body.temperature, 20f, 44f),
                Adrenaline = Mathf.Max(0f, totalAdrenaline / 100f),
                AllowAtpRecovery = body.alive && body.bloodPressure > 60f && body.bloodOxygen > 50f
            };
        }

        /// <summary>
        /// Compute target blood pressure replacing vanilla heart-rate pump factor with Left Ventricular ejection output.
        /// </summary>
        public static float ComputeBloodPressureFromLvEjection(Body body, Painkillers? painkillers, in CardiacTelemetrySnapshot snap)
        {
            if (!body.alive)
                return 0f;

            // Left Ventricular output factor: LV_CO = LV_SV * HR / 1000
            // During VF, LV_SV ~ 0 -> lvPumpFactor -> 0 -> shock/collapse.
            // Above normal CO, baroreceptor-damped curve matches vanilla range (~1.0 - 1.72).
            float coRatio = Mathf.Max(0f, snap.LeftVentricularCardiacOutputLpm) / NormalLvCardiacOutputLpm;
            float lvPumpFactor = coRatio <= 1.0f
                ? Mathf.Clamp01(coRatio)
                : 1.0f + Mathf.Min(0.72f, (coRatio - 1.0f) * 0.45f);

            float bloodVolFactor = 1f + (body.bloodVolumePercentage - 1f) * 1.1f;
            float viscosityFactor = 1f + body.bloodViscosity / 200f;
            float sepsisFactor = 1f - body.septicShock * 0.00525f;
            float opiateFactor = 1f;
            if (painkillers != null)
            {
                opiateFactor = Mathf.Clamp(1f - painkillers.actualOpiateReception / 400f, 0.75f, 1.25f);
            }
            float tempFactor = 1f - body.tempDiffFromNormal / 40f;
            float weightFactor = 1f + body.weightOffset / 180f;

            float targetBp = 120f * lvPumpFactor * bloodVolFactor * viscosityFactor *
                             body.thirstBloodPressure * sepsisFactor * opiateFactor *
                             tempFactor * weightFactor / Mathf.Max(0.25f, body.bloodVesselSize);

            if (body.bloodPressureChangeFromMedicine > 0f)
                targetBp *= 0.75f;
            if (body.bloodPressureChangeFromMedicine < 0f)
                targetBp *= 1.25f;

            return Mathf.Clamp(targetBp, 0f, 250f);
        }

        public static bool IsVentricularFibrillation(Body? body)
        {
            if (body == null) return false;
            var ctrl = GetOrCreate(body);
            ctrl.LastSnapshot = ctrl.Worker.GetTelemetrySnapshot();
            return ctrl.LastSnapshot.Rhythm == RhythmType.VentricularFibrillation;
        }

        public static void AdministerAmiodarone(Body? body, float ml)
        {
            if (body == null || ml <= 0f) return;
            var ctrl = GetOrCreate(body);
            // 20 mL ampoule = 150 mg IV loading bolus
            float doseMg = ml * 7.5f;
            ctrl.Worker.EnqueueCommand(sim => sim.AdministerAmiodarone(doseMg));
        }

        /// <summary>
        /// Intravenous Lidocaine administration: 10 mL = 100 mg loading dose (2-min half-life).
        /// </summary>
        public static void AdministerLidocaine(Body? body, float ml)
        {
            if (body == null || ml <= 0f) return;
            var ctrl = GetOrCreate(body);
            float doseMg = ml * 10.0f;
            ctrl.Worker.EnqueueCommand(sim => sim.AdministerLidocaine(doseMg));
        }

        /// <summary>
        /// Epinephrine dosing: fills endogenous Body.adrenaline first up to 100,
        /// then overflows any excess into uncapped ExogenousAdrenaline.
        /// </summary>
        public static void AdministerEpinephrine(Body? body, float ml, bool intravenous)
        {
            if (body == null || ml <= 0f) return;
            var ctrl = GetOrCreate(body);

            // Standard 20 mL syringe = 100 adrenaline units (oral absorption is ~1/6th of IV)
            float dose = ml * (intravenous ? 5.0f : 0.85f);

            float headroom = Mathf.Max(0f, 100f - body.adrenaline);
            float toEndo = Mathf.Min(dose, headroom);
            body.adrenaline = Mathf.Clamp(body.adrenaline + toEndo, 0f, 100f);

            float overflow = dose - toEndo;
            if (overflow > 0f)
            {
                ctrl.ExogenousAdrenalineDepot += overflow;
            }
        }

        public static void Defibrillate(Body? body, float energyJoules)
        {
            if (body == null || energyJoules <= 0f) return;
            var ctrl = GetOrCreate(body);
            ctrl.Worker.EnqueueCommand(sim => sim.Defibrillate(energyJoules));
        }

        public static void InduceVentricularFibrillation(Body? body)
        {
            if (body == null) return;
            var ctrl = GetOrCreate(body);
            ctrl.Worker.EnqueueCommand(sim => sim.InduceVentricularFibrillation());
        }

        public static void TriggerPrematureBeat(Body? body)
        {
            if (body == null) return;
            var ctrl = GetOrCreate(body);
            ctrl.Worker.EnqueueCommand(sim => sim.TriggerPrematureVentricularBeat());
        }

        public static void ResetToSinus(Body? body, bool clearDrugs = false, float targetBpm = 72f)
        {
            if (body == null) return;
            var ctrl = GetOrCreate(body);
            ctrl.MyocardialIschemiaLevel = 0f;
            if (clearDrugs)
            {
                ctrl.ExogenousAdrenaline = 0f;
                ctrl.ExogenousAdrenalineDepot = 0f;
            }
            ctrl.Worker.ResetImmediateToSinus(clearDrugs, targetBpm);
            ctrl.LastSnapshot = ctrl.Worker.GetTelemetrySnapshot();
            ctrl.PrevBloodPressure = body.bloodPressure;
        }

        public static void OpenOrToggleMultiLeadMinigame(Body? body, Item? item = null)
        {
            if (body == null || MinigameBase.main == null || !body.alive || !body.conscious)
                return;

            // Hotkey requires biochip; ECG Monitor item bypasses chip restriction
            if (item == null && WorldGeneration.unchipped)
                return;

            if (MinigameBase.main.currentMinigame is MultiLeadEcgMinigame)
            {
                MinigameBase.main.EndMinigame();
                return;
            }

            if (MinigameBase.main.currentMinigame)
                return;

            MinigameBase.main.StartMinigame(new MultiLeadEcgMinigame(body, item != null), item);
        }

        public static float SampleLeadForEcgVisualizer(Body? body, float offsetSec, float windowSec)
        {
            if (body == null || !body.alive) return 0f;
            if (body.defibShockedFrames > 0)
                return (UnityEngine.Random.value > 0.5f) ? 1f : -1f;

            var ctrl = GetOrCreate(body);
            return Mathf.Clamp(
                ctrl.Worker.SampleLeadPeakPreserving(ctrl.SelectedLead, Mathf.Max(0f, offsetSec), Mathf.Max(0.002f, windowSec)),
                -1f,
                1f);
        }
    }
}
