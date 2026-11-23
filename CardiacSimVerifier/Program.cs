using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CardiacSimCore;

namespace CardiacSimVerifier
{
    internal static class Program
    {
        private sealed class RecordedPoint
        {
            public float T;
            public float I;
            public float II;
            public float III;
            public float aVR;
            public float aVL;
            public float aVF;
            public float LvSv;
            public float RvSv;
            public float Hr;
        }

        private static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Casualties Unknown Cardiac Grid Electrophysiology Verifier ===\n");

            var sim = new CardiacGridSimulator();
            Console.WriteLine($"Anatomy Active Cells: {sim.Anatomy.CellCount} " +
                              $"(SA:{sim.Anatomy.SaNodeCells.Length}, LV:{sim.Anatomy.LvCells.Length}, " +
                              $"RV:{sim.Anatomy.RvCells.Length}, Septum:{sim.Anatomy.SeptumCells.Length})\n");

            if (Array.Exists(args, a => a == "--beatdiag"))
            {
                sim.ResetToSinus();
                for (int ms = 0; ms <= 600; ms++)
                {
                    sim.StepOneMillisecond();
                    if (ms % 20 == 0)
                        Console.WriteLine($"t={ms,3}ms II={sim.LatestSample.II:+0.000;-0.000} | {sim.GetRegionPhaseDebug()}");
                }
                return;
            }
            if (Array.Exists(args, a => a == "--vfdiag"))
            {
                sim.ResetToSinus();
                sim.InduceVentricularFibrillation();
                for (int ms = 0; ms <= 1500; ms++)
                {
                    sim.StepOneMillisecond();
                    if (ms % 50 == 0)
                        Console.WriteLine($"VF t={ms,4}ms II={sim.LatestSample.II:+0.000;-0.000} VFrac={sim.VentricularActiveFraction:F2} Sync={sim.VentricularSynchrony:F2} | {sim.GetRegionPhaseDebug()}");
                }
                Console.WriteLine("--- VT diag ---");
                sim.ResetToSinus();
                sim.InduceVentricularTachycardia();
                for (int ms = 0; ms <= 1500; ms++)
                {
                    sim.StepOneMillisecond();
                    if (ms % 50 == 0)
                        Console.WriteLine($"VT t={ms,4}ms II={sim.LatestSample.II:+0.000;-0.000} VFrac={sim.VentricularActiveFraction:F2} Sync={sim.VentricularSynchrony:F2} | {sim.GetRegionPhaseDebug()}");
                }
                return;
            }

            var scenarios = new List<(string title, List<RecordedPoint> trace)>();

            // 1. Normal Sinus Rhythm (72 BPM)
            var normalTrace = RunScenario("1. Normal Sinus Rhythm (72 BPM)", sim, 5.0f, reset: true);
            AnalyzeSinusBeat(normalTrace, "Normal Sinus (72 BPM)");
            VerifyFrontalIdentities(normalTrace);
            scenarios.Add(("Normal Sinus (72 BPM)", normalTrace));

            // 2. Sinus Tachycardia (140 BPM)
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.Environment.TargetSinusBpm = 140f;
            var tachyTrace = RunScenario("2. Sinus Tachycardia (140 BPM)", sim, 4.0f, reset: true);
            AnalyzeSinusBeat(tachyTrace, "Sinus Tachycardia (140 BPM)");
            scenarios.Add(("Sinus Tachycardia (140 BPM)", tachyTrace));

            // 3. Sinus Bradycardia (46 BPM)
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.Environment.TargetSinusBpm = 46f;
            var bradyTrace = RunScenario("3. Sinus Bradycardia (46 BPM)", sim, 5.5f, reset: true);
            AnalyzeSinusBeat(bradyTrace, "Sinus Bradycardia (46 BPM)");
            scenarios.Add(("Sinus Bradycardia (46 BPM)", bradyTrace));

            // 4. Focal LV Transmural Ischemia (STEMI-like ST Elevation)
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.Environment.FocalTransmuralIschemia = 0.72f;
            var stemiTrace = RunScenario("4. Focal LV Transmural Ischemia (0.72)", sim, 5.0f, reset: true);
            AnalyzeSinusBeat(stemiTrace, "Transmural Ischemia");
            scenarios.Add(("LV Transmural Ischemia (ST Elevation)", stemiTrace));

            // 5. Subendocardial Ischemia (NSTEMI-like ST Depression)
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.Environment.SubendocardialIschemia = 0.80f;
            var nstemiTrace = RunScenario("5. Subendocardial Ischemia (0.80)", sim, 5.0f, reset: true);
            AnalyzeSinusBeat(nstemiTrace, "Subendocardial Ischemia");
            scenarios.Add(("LV Subendocardial Ischemia (ST Depression)", nstemiTrace));

            // 6. RV Ischemia (LV vs RV ejection divergence)
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.Environment.RightVentricularIschemia = 0.85f;
            var rvIschTrace = RunScenario("6. Right Ventricular Ischemia (0.85)", sim, 4.0f, reset: true);
            scenarios.Add(("RV Ischemia", rvIschTrace));

            // 7. Amiodarone Pharmacokinetics (5-min = 300s half-life) & QT prolongation
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.ResetToSinus();
            sim.AmiodaroneConcentration = 0f;
            sim.AdministerAmiodarone(220f);
            // Warm up 20s for IV absorption peak
            RunScenario("7a. Amiodarone Loading (20s)", sim, 20.0f, reset: false);
            float peakAmio = sim.AmiodaroneConcentration;
            var amioTrace = RunScenario("7b. Amiodarone Steady Effect (5s)", sim, 5.0f, reset: false);
            AnalyzeSinusBeat(amioTrace, "Amiodarone Loaded");
            scenarios.Add(("Amiodarone Loaded (QT Prolongation)", amioTrace));

            // Advance 300 seconds (5 minutes game time) in larger steps to verify half-life
            for (int sec = 0; sec < 300; sec++)
                sim.Step(1.0f);
            float afterHalfLife = sim.AmiodaroneConcentration;
            Console.WriteLine($"[PK Check] Amiodarone peak={peakAmio:F3} mg/L -> after 300s={afterHalfLife:F3} mg/L " +
                              $"(ratio={afterHalfLife / Math.Max(0.0001f, peakAmio):F3}, expected ~0.500)\n");

            // 8. VF -> Low Energy Defib (30 J fail) -> High Energy Defib (180 J success)
            sim.AmiodaroneConcentration = 0f;
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.ResetToSinus();
            RunScenario("8a. Baseline before VF", sim, 2.5f, reset: false);
            sim.InduceVentricularFibrillation();
            var vfTrace = RunScenario("8b. Ventricular Fibrillation (VF)", sim, 3.0f, reset: false);
            scenarios.Add(("Ventricular Fibrillation (VF)", vfTrace));

            sim.Defibrillate(30f);
            var lowDefibTrace = RunScenario("8c. After Low Energy Shock (30 J)", sim, 3.0f, reset: false);
            scenarios.Add(("After 30 J Shock", lowDefibTrace));

            sim.Defibrillate(180f);
            var highDefibTrace = RunScenario("8d. After High Energy Shock (180 J)", sim, 4.0f, reset: false);
            scenarios.Add(("After 180 J Defibrillation", highDefibTrace));

            // 9. VT + Defibrillation (100 J)
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.ResetToSinus();
            RunScenario("9a. Pre-VT", sim, 2.0f, reset: false);
            sim.InduceVentricularTachycardia();
            var vtTrace = RunScenario("9b. Ventricular Tachycardia (VT)", sim, 2.5f, reset: false);
            sim.Defibrillate(100f);
            var syncCardioTrace = RunScenario("9c. VT Defibrillation (100 J)", sim, 3.5f, reset: false);
            scenarios.Add(("VT -> Defibrillation (100 J)", ConcatTraces(vtTrace, syncCardioTrace)));

            // 10. Lidocaine suppressing severe ischemic ectopy & preventing VF recurrence
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.Environment.FocalTransmuralIschemia = 0.85f;
            sim.ResetToSinus();
            var ischNoLido = RunScenario("10a. Severe Ischemia WITHOUT Lidocaine", sim, 6.0f, reset: false);
            sim.AdministerLidocaine(100f);
            RunScenario("10b. Lidocaine Loading + 180 J Defib (8s)", sim, 4.0f, reset: false);
            sim.Defibrillate(180f);
            RunScenario("10b2. Post-Defib under Lidocaine (4s)", sim, 4.0f, reset: false);
            var ischWithLido = RunScenario("10c. Severe Ischemia WITH Lidocaine (prevents VF)", sim, 6.0f, reset: false);
            scenarios.Add(("Severe Ischemia + Lidocaine", ischWithLido));

            // 11. Untreated VF -> Fine VF -> Asystole progression (ATP depletion)
            sim.LidocaineConcentration = 0f;
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.ResetToSinus();
            sim.InduceVentricularFibrillation();
            for (int sec = 0; sec < 85; sec++)
                sim.Step(1.0f);
            var asystoleTrace = RunScenario("11. Untreated VF after 85s -> Asystole", sim, 3.0f, reset: false);
            Console.WriteLine($"  [ATP Check] Rhythm={sim.CurrentRhythm}, ATP={sim.MyocardialAtpReserve:F3}\n");
            scenarios.Add(("Untreated VF -> Asystole", asystoleTrace));

            // 12. CPR / AutoPump (BP > 60 && SpO2 > 50): Asystole -> Fine VF -> Coarse VF -> Defib ROSC
            sim.Environment.AllowAtpRecovery = true;
            for (int sec = 0; sec < 7; sec++)
                sim.Step(1.0f);
            var fineVfTrace = RunScenario("12a. CPR 7s (Asystole -> Fine VF)", sim, 2.5f, reset: false);
            float fineVfAtp = sim.MyocardialAtpReserve;
            float fineVfP2P = MeasureLeadIIPeakToPeak(fineVfTrace);

            for (int sec = 0; sec < 18; sec++)
                sim.Step(1.0f);
            var coarseVfTrace = RunScenario("12b. CPR +18s (Fine VF -> Coarse VF)", sim, 2.5f, reset: false);
            float coarseVfAtp = sim.MyocardialAtpReserve;
            float coarseVfP2P = MeasureLeadIIPeakToPeak(coarseVfTrace);

            sim.Defibrillate(180f);
            var cprRoscTrace = RunScenario("12c. Post-CPR Defibrillation (180 J)", sim, 4.0f, reset: false);
            Console.WriteLine($"  [CPR Resuscitation Check] FineVF(ATP={fineVfAtp:F3}, II_p2p={fineVfP2P:F3}mV) -> " +
                              $"CoarseVF(ATP={coarseVfAtp:F3}, II_p2p={coarseVfP2P:F3}mV) -> PostDefib({sim.CurrentRhythm})\n");
            scenarios.Add(("CPR: Asystole -> Fine VF -> Coarse VF -> ROSC", ConcatTraces(fineVfTrace, coarseVfTrace)));
            if (coarseVfP2P <= fineVfP2P * 1.4f || sim.CurrentRhythm != RhythmType.NormalSinus)
                throw new InvalidOperationException("CPR resuscitation progression check failed!");

            // 13. Moderate Ischemia -> Isolated PVCs (no premature jump to VF) -> Moderately Severe Ischemia -> Organized VT
            sim.LidocaineConcentration = 0f;
            sim.AmiodaroneConcentration = 0f;
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.Environment.GlobalIschemia = 0.48f;
            sim.ResetToSinus();
            var modIschPvcTrace = RunScenario("13a. Moderate Ischemia (0.48) -> Isolated PVCs without VF", sim, 8.0f, reset: false);
            Console.WriteLine($"  [Mod Ischemia Check] Rhythm={sim.CurrentRhythm}, Sync={sim.VentricularSynchrony:F2}\n");
            if (sim.CurrentRhythm == RhythmType.VentricularFibrillation)
                throw new InvalidOperationException("Moderate ischemia jumped prematurely to VF!");

            sim.Environment.GlobalIschemia = 0.76f;
            var modSevereVtTrace = RunScenario("13b. High Ischemia (0.76) -> Expect VT / Ectopy", sim, 8.0f, reset: false);
            Console.WriteLine($"  [High Ischemia Check] Rhythm={sim.CurrentRhythm}, HR={sim.MeasuredHeartRateBpm:F1}\n");

            // 14. Dual Adrenaline Overdose (Total = 260 -> Adrenaline = 2.60): faster conduction + calcium overload
            sim.Environment = CardiacEnvironmentInput.Normal();
            sim.Environment.Adrenaline = 1.0f;
            sim.ResetToSinus();
            var endoAdrenTrace = RunScenario("14a. Max Endogenous Adrenaline (1.0)", sim, 3.0f, reset: false);
            AnalyzeSinusBeat(endoAdrenTrace, "Endo Adren 1.0");

            sim.Environment.Adrenaline = 2.6f;
            var exoOverdoseTrace = RunScenario("14b. Exogenous Adrenaline Overdose (2.6)", sim, 5.0f, reset: false);
            Console.WriteLine($"  [Exogenous Overdose Check] Rhythm={sim.CurrentRhythm}, HR={sim.MeasuredHeartRateBpm:F1}, LV_SV={sim.GetLeftVentricularEjectionVolumeMl():F1} mL\n");

            // 15. V1.1.0 Real-Time Pulsatile Arterial Pressure (Normal Sinus 72 BPM -> ~120/80, MAP ~93, Beat LV SV)
            sim.ResetToSinus(clearDrugs: true, resetEnvironment: true, targetBpm: 72f);
            sim.Step(8.0f);
            Console.WriteLine("--- 15. Real-Time Pulsatile ABP (Normal Sinus 72 BPM) ---");
            Console.WriteLine($"  ABP_5s={sim.SystolicBp5s:F1}/{sim.DiastolicBp5s:F1} mmHg (MAP={sim.MeanArterialPressure5s:F1}), " +
                              $"LastBeat_LV_SV={sim.GetLastBeatLvStrokeVolumeMl():F1} mL, Beats={sim.CompletedBeatCount}, " +
                              $"Tone={sim.EffectiveVascularTone:F2}, Diam={sim.EffectiveVesselDiameter:F2}\n");
            if (sim.SystolicBp5s < 105f || sim.SystolicBp5s > 138f || sim.DiastolicBp5s < 65f || sim.DiastolicBp5s > 92f)
                throw new InvalidOperationException($"Normal sinus ABP out of physiological range: {sim.SystolicBp5s:F1}/{sim.DiastolicBp5s:F1}");

            // 16. VF Arterial Collapse + AutoPump Mechanical CPR Pulse (every 0.3s -> sustains MAP > 46.5 / BP > 60)
            sim.InduceVentricularFibrillation();
            sim.Step(8.0f);
            float vfCollapseMap = sim.MeanArterialPressure5s;
            float vfCollapseSbp = sim.SystolicBp5s;
            float vfCollapseDbp = sim.DiastolicBp5s;
            // Now simulate AutoPump firing every 0.3s while vanilla-equiv BP < 85 (MAP < 66)
            for (int tick = 0; tick < 40; tick++)
            {
                float vanillaBpEq = sim.MeanArterialPressure5s * (120f / 93.333f);
                if (vanillaBpEq < 85f)
                    sim.InjectCprPulse(26f);
                sim.Step(0.3f);
            }
            float cprMap = sim.MeanArterialPressure5s;
            float cprVanillaBp = cprMap * (120f / 93.333f);
            Console.WriteLine("--- 16. VF Collapse -> AutoPump CPR Pulses ---");
            Console.WriteLine($"  Untreated VF ABP: {vfCollapseSbp:F1}/{vfCollapseDbp:F1} (MAP={vfCollapseMap:F1}) -> " +
                              $"AutoPump CPR ABP: {sim.SystolicBp5s:F1}/{sim.DiastolicBp5s:F1} (MAP={cprMap:F1}, VanillaBP={cprVanillaBp:F1})\n");
            if (vfCollapseMap > 28f || cprVanillaBp <= 60f)
                throw new InvalidOperationException($"VF collapse / CPR pulse check failed! vfMAP={vfCollapseMap:F1}, cprVanillaBP={cprVanillaBp:F1}");

            // 17. Severe Hypertensive Crisis (Exogenous Epinephrine) -> Rapid Reversal via Dose-Dependent Nitroprusside (t1/2 = 50s)
            sim.ResetToSinus(clearDrugs: true, resetEnvironment: true, targetBpm: 92f);
            sim.Environment.Adrenaline = 2.4f;
            sim.Step(10.0f);
            float crisisSbp = sim.SystolicBp5s;
            float crisisDbp = sim.DiastolicBp5s;
            string crisisInfo = $"Rhythm={sim.CurrentRhythm}, HR={sim.MeasuredHeartRateBpm:F1}, Sync={sim.VentricularSynchrony:F2}, Tone={sim.EffectiveVascularTone:F2}, LastSV={sim.GetLastBeatLvStrokeVolumeMl():F1}";
            sim.AdministerNitroprusside(2.5f); // 25 mL IV bolus
            sim.Step(10.0f);
            float postNitroSbp = sim.SystolicBp5s;
            float postNitroDbp = sim.DiastolicBp5s;
            Console.WriteLine("--- 17. Epinephrine Hypertensive Crisis -> Nitroprusside Reversal ---");
            Console.WriteLine($"  Crisis ABP={crisisSbp:F1}/{crisisDbp:F1} mmHg ({crisisInfo})");
            Console.WriteLine($"  Post-Nitroprusside (10s) ABP={postNitroSbp:F1}/{postNitroDbp:F1} mmHg (Rhythm={sim.CurrentRhythm}, NitroConc={sim.NitroprussideConcentration:F2}, Tone={sim.EffectiveVascularTone:F2})\n");
            if (crisisSbp < 190f || postNitroSbp >= crisisSbp - 50f)
                throw new InvalidOperationException($"Hypertensive crisis / Nitroprusside reversal check failed! crisis={crisisSbp:F1}/{crisisDbp:F1}, post={postNitroSbp:F1}/{postNitroDbp:F1}");

            // 18. Pure Dose-Dependent Vasopressin / ADH (t1/2 = 120s)
            sim.ResetToSinus(clearDrugs: true, resetEnvironment: true, targetBpm: 72f);
            sim.Environment.VascularTone = 0.55f; // Septic/vasoplegic shock baseline
            sim.Step(8.0f);
            float shockMap = sim.MeanArterialPressure5s;
            float shockHr = sim.MeasuredHeartRateBpm;
            sim.AdministerVasopressin(2.0f); // 20 mL IV bolus
            sim.Step(12.0f);
            float postVasoMap = sim.MeanArterialPressure5s;
            float postVasoHr = sim.MeasuredHeartRateBpm;
            Console.WriteLine("--- 18. Vasoplegia -> Vasopressin (ADH, t1/2=120s) Pure V1 Pressor ---");
            Console.WriteLine($"  Vasoplegic MAP={shockMap:F1} (HR={shockHr:F1}) -> Post-ADH MAP={postVasoMap:F1} (HR={postVasoHr:F1}, VasoConc={sim.VasopressinConcentration:F2}, Tone={sim.EffectiveVascularTone:F2})\n");
            if (postVasoMap <= shockMap + 15f)
                throw new InvalidOperationException($"Vasopressin pressor effect too weak! shockMAP={shockMap:F1}, postMAP={postVasoMap:F1}");

            // 19. Clinical Dose-Staged Dopamine (t1/2 = 75s): Low/Mod beta1 inotropy+HR -> High alpha1 vasoconstriction
            sim.ResetToSinus(clearDrugs: true, resetEnvironment: true, targetBpm: 68f);
            sim.Step(6.0f);
            float baseHr = sim.MeasuredHeartRateBpm;
            float baseTone = sim.EffectiveVascularTone;
            float baseMap = sim.MeanArterialPressure5s;
            sim.AdministerDopamine(1.0f); // 10 mL IV -> beta1 range
            sim.Step(9.0f);
            float modDopaHr = sim.MeasuredHeartRateBpm;
            float modDopaTone = sim.EffectiveVascularTone;
            float modDopaMap = sim.MeanArterialPressure5s;
            sim.AdministerDopamine(2.5f); // additional 25 mL IV -> high-dose alpha1 pressor
            sim.Step(9.0f);
            float highDopaTone = sim.EffectiveVascularTone;
            float highDopaMap = sim.MeanArterialPressure5s;
            Console.WriteLine("--- 19. Clinical Dose-Staged Dopamine (t1/2=75s) ---");
            Console.WriteLine($"  Base: HR={baseHr:F1}, Tone={baseTone:F2}, MAP={baseMap:F1} | " +
                              $"Mod(beta1): HR={modDopaHr:F1}, Tone={modDopaTone:F2}, MAP={modDopaMap:F1} | " +
                              $"High(alpha1): Tone={highDopaTone:F2}, MAP={highDopaMap:F1} (SBP={sim.SystolicBp5s:F1}/{sim.DiastolicBp5s:F1})\n");
            if (modDopaHr <= baseHr + 5f || highDopaTone <= modDopaTone + 0.25f || highDopaMap <= modDopaMap + 12f)
                throw new InvalidOperationException("Dopamine dose-staged beta1 -> alpha1 check failed!");

            // 20. Performance & 20x Worker Thread Benchmark
            RunPerformanceBenchmarks();

            // Write interactive HTML report to scratch for visual inspection if desired
            string? outHtml = args.Length > 0 ? args[0] : null;
            if (!string.IsNullOrWhiteSpace(outHtml))
            {
                WriteHtmlReport(outHtml, scenarios);
                Console.WriteLine($"HTML waveform report written to: {outHtml}");
            }
        }

        private static List<RecordedPoint> RunScenario(string name, CardiacGridSimulator sim, float durationSec, bool reset)
        {
            if (reset) sim.ResetToSinus();

            var trace = new List<RecordedPoint>((int)(durationSec * 500));
            int msTotal = (int)Math.Round(durationSec * 1000f);
            for (int m = 0; m < msTotal; m++)
            {
                sim.StepOneMillisecond();
                if ((m & 1) == 0) // 500 Hz record
                {
                    var s = sim.LatestSample;
                    trace.Add(new RecordedPoint
                    {
                        T = s.TimeSec,
                        I = s.I,
                        II = s.II,
                        III = s.III,
                        aVR = s.aVR,
                        aVL = s.aVL,
                        aVF = s.aVF,
                        LvSv = sim.GetLeftVentricularEjectionVolumeMl(),
                        RvSv = sim.GetRightVentricularEjectionVolumeMl(),
                        Hr = sim.MeasuredHeartRateBpm
                    });
                }
            }

            var last = trace[trace.Count - 1];
            Console.WriteLine($"--- {name} ---");
            Console.WriteLine($"  Rhythm={sim.CurrentRhythm}, HR={sim.MeasuredHeartRateBpm:F1} bpm, " +
                              $"Sync={sim.VentricularSynchrony:F2}, LV_SV={last.LvSv:F1} mL, RV_SV={last.RvSv:F1} mL, " +
                              $"LV_CO={sim.GetLeftVentricularCardiacOutputLpm():F2} L/min");
            PrintAsciiWave(trace, 2.4f);
            Console.WriteLine();
            return trace;
        }

        private static float MeasureLeadIIPeakToPeak(List<RecordedPoint> trace)
        {
            if (trace.Count == 0) return 0f;
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < trace.Count; i++)
            {
                if (trace[i].II < min) min = trace[i].II;
                if (trace[i].II > max) max = trace[i].II;
            }
            return max - min;
        }

        private static void AnalyzeSinusBeat(List<RecordedPoint> trace, string label)
        {
            // Find the last complete R-peak in Lead II (at least 0.6s from end, after 1.5s warmup)
            int rIdx = -1;
            float rPeak = float.MinValue;
            int startSearch = (int)(trace.Count * 0.35);
            int endSearch = trace.Count - (int)(0.55f * 500);
            for (int i = startSearch; i < endSearch; i++)
            {
                if (trace[i].II > rPeak)
                {
                    rPeak = trace[i].II;
                    rIdx = i;
                }
            }
            if (rIdx < 150) return;

            // Search window around R peak (500 Hz -> 1 sample = 2 ms)
            // P wave: [-240 ms .. -80 ms] before R
            float pMax = float.MinValue;
            int pIdx = rIdx - 80;
            for (int i = Math.Max(0, rIdx - 130); i <= rIdx - 40; i++)
            {
                if (trace[i].II > pMax) { pMax = trace[i].II; pIdx = i; }
            }
            float pInAvr = trace[pIdx].aVR;

            // Q dip: [-32 ms .. -4 ms]
            float qMin = float.MaxValue;
            for (int i = Math.Max(0, rIdx - 16); i <= rIdx - 2; i++)
                if (trace[i].II < qMin) qMin = trace[i].II;

            // S dip: [+4 ms .. +50 ms]
            float sMin = float.MaxValue;
            int sIdx = rIdx + 10;
            for (int i = rIdx + 2; i <= Math.Min(trace.Count - 1, rIdx + 28); i++)
            {
                if (trace[i].II < sMin) { sMin = trace[i].II; sIdx = i; }
            }

            // QRS width (where |II| > 0.08 around rIdx, capped within +/- 65 ms)
            int qrsStart = rIdx;
            while (qrsStart > Math.Max(0, rIdx - 32) && Math.Abs(trace[qrsStart].II) > 0.06f * rPeak) qrsStart--;
            int qrsEnd = sIdx;
            while (qrsEnd < Math.Min(trace.Count - 1, rIdx + 45) && Math.Abs(trace[qrsEnd].II) > 0.06f * rPeak) qrsEnd++;
            float qrsMs = (qrsEnd - qrsStart) * 2f;

            // J+20ms & J+60ms ST segment level
            int st20Idx = Math.Min(trace.Count - 1, qrsEnd + 10); // +20 ms
            int stIdx = Math.Min(trace.Count - 1, qrsEnd + 30); // +60 ms
            float st20Level = trace[st20Idx].II;
            float stLevel = trace[stIdx].II;

            // T wave peak: [+90 ms .. +380 ms] after qrsEnd
            float tPeak = float.MinValue;
            float tMin = float.MaxValue;
            int tEndIdx = Math.Min(trace.Count - 1, qrsStart + 220);
            int tPeakIdx = qrsEnd + 45;
            for (int i = Math.Min(trace.Count - 1, qrsEnd + 25); i < tEndIdx; i++)
            {
                if (trace[i].II > tPeak) { tPeak = trace[i].II; tPeakIdx = i; }
                if (trace[i].II < tMin) tMin = trace[i].II;
            }
            int tEnd = tPeakIdx;
            while (tEnd < Math.Min(trace.Count - 1, qrsStart + 250) && Math.Abs(trace[tEnd].II) > 0.04f * Math.Max(0.15f, Math.Abs(tPeak))) tEnd++;

            // P onset for PR interval
            int pOnset = pIdx;
            while (pOnset > Math.Max(0, pIdx - 40) && trace[pOnset].II > 0.02f) pOnset--;
            float prMs = (qrsStart - pOnset) * 2f;
            float qtMs = (tEnd - qrsStart) * 2f;
            float rrSec = 60f / Math.Max(30f, trace[rIdx].Hr);
            float qtcMs = qtMs / (float)Math.Sqrt(rrSec);

            Console.WriteLine($"  [ECG Metrics - {label}] Lead II:");
            Console.WriteLine($"    P={pMax:F3} (aVR={pInAvr:F3}), Q={qMin:F3}, R={rPeak:F3}, S={sMin:F3}, ST(J+20)={st20Level:+0.000;-0.000}, ST(J+60)={stLevel:+0.000;-0.000}, T_max={tPeak:F3}, T_min={tMin:F3}");
            Console.WriteLine($"    PR={prMs:F0} ms, QRS={qrsMs:F0} ms, QT={qtMs:F0} ms, QTc={qtcMs:F0} ms");
            Console.WriteLine($"    Other Leads at R-peak: I={trace[rIdx].I:F3}, II={trace[rIdx].II:F3}, III={trace[rIdx].III:F3}, aVR={trace[rIdx].aVR:F3}, aVL={trace[rIdx].aVL:F3}, aVF={trace[rIdx].aVF:F3}");
            if (label.Contains("Normal Sinus"))
            {
                Console.Write("    Beat curve (rel R ms -> II): ");
                for (int dtMs = -200; dtMs <= 360; dtMs += 20)
                {
                    int k = rIdx + dtMs / 2;
                    if (k >= 0 && k < trace.Count)
                        Console.Write($"{dtMs:+0;-0;0}:{trace[k].II:+0.00;-0.00} ");
                }
                Console.WriteLine();
            }
        }

        private static void VerifyFrontalIdentities(List<RecordedPoint> trace)
        {
            double maxErrEinthoven = 0;
            double maxErrGoldberger = 0;
            foreach (var p in trace)
            {
                maxErrEinthoven = Math.Max(maxErrEinthoven, Math.Abs((p.I + p.III) - p.II));
                maxErrGoldberger = Math.Max(maxErrGoldberger, Math.Abs(p.aVR + p.aVL + p.aVF));
            }
            Console.WriteLine($"  [Frontal Lead Math Check] max|I+III-II|={maxErrEinthoven:E2}, max|aVR+aVL+aVF|={maxErrGoldberger:E2}");
        }

        private static void PrintAsciiWave(List<RecordedPoint> trace, float windowSec)
        {
            int count = Math.Min(trace.Count, (int)(windowSec * 500));
            int start = trace.Count - count;
            const int cols = 88;
            const int rows = 9;
            var grid = new char[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    grid[r, c] = (r == rows / 2) ? '-' : ' ';

            float maxAbs = 0.6f;
            for (int i = start; i < trace.Count; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(trace[i].II));

            for (int c = 0; c < cols; c++)
            {
                int i0 = start + (c * count) / cols;
                int i1 = start + ((c + 1) * count) / cols;
                float peak = 0f;
                for (int i = i0; i < i1 && i < trace.Count; i++)
                {
                    if (Math.Abs(trace[i].II) > Math.Abs(peak))
                        peak = trace[i].II;
                }
                int row = (int)Math.Round((1f - (peak / maxAbs)) * 0.5f * (rows - 1));
                row = Math.Clamp(row, 0, rows - 1);
                grid[row, c] = '*';
            }

            for (int r = 0; r < rows; r++)
            {
                var line = new char[cols];
                for (int c = 0; c < cols; c++) line[c] = grid[r, c];
                Console.WriteLine("    |" + new string(line) + "|");
            }
        }

        private static void RunPerformanceBenchmarks()
        {
            Console.WriteLine("=== Performance & 20x Speed Multi-Thread Benchmark ===");
            var rawSim = new CardiacGridSimulator();
            var sw = Stopwatch.StartNew();
            // Simulate 200 seconds of game time (200,000 ms steps = 10 seconds at 20x speed!)
            for (int i = 0; i < 200; i++)
                rawSim.Step(1.0f);
            sw.Stop();
            double sim200SecMs = sw.Elapsed.TotalMilliseconds;
            double perGameSecMs = sim200SecMs / 200.0;
            double cpuPctAt20x = (perGameSecMs * 20.0) / 10.0; // ms per 1000ms * 100%
            Console.WriteLine($"  Single-thread raw throughput: 200 game-sec (200,000 steps) in {sim200SecMs:F1} ms " +
                              $"({perGameSecMs:F2} ms / game-sec => {cpuPctAt20x:F1}% of 1 core at 20x speed)");

            // Test dedicated worker thread running 60 FPS frames at 20x speed for 2.0 real seconds (= 40 game seconds)
            using var worker = new CardiacWorkerRunner();
            sw.Restart();
            double mainThreadTimeMs = 0;
            int frames = 120; // 2 real seconds at 60 FPS
            for (int f = 0; f < frames; f++)
            {
                var t0 = Stopwatch.GetTimestamp();
                // Each 16.67ms real frame at 20x speed = 0.3333s game time
                worker.EnqueueDeltaTime(0.333333f);
                var snap = worker.GetTelemetrySnapshot();
                float ecg = worker.SampleLeadPeakPreserving(FrontalLead.II, 0f, 0.028f);
                var t1 = Stopwatch.GetTimestamp();
                mainThreadTimeMs += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                Thread.Sleep(16);
            }
            // Wait briefly for last batch
            Thread.Sleep(50);
            var finalSnap = worker.GetTelemetrySnapshot();
            Console.WriteLine($"  Worker 20x Real-Time Test (120 frames @ 60fps x 20x): " +
                              $"GameTime={finalSnap.SimulationTimeSec:F2}s / 40.00s, Steps={finalSnap.TotalStepsProcessed}, " +
                              $"MainThreadAvg={mainThreadTimeMs / frames:F4} ms/frame, LastWorkerBatch={finalSnap.LastBatchComputeMs:F2} ms");
        }

        private static List<RecordedPoint> ConcatTraces(List<RecordedPoint> a, List<RecordedPoint> b)
        {
            var res = new List<RecordedPoint>(a.Count + b.Count);
            res.AddRange(a);
            res.AddRange(b);
            return res;
        }

        private static void WriteHtmlReport(string path, List<(string title, List<RecordedPoint> trace)> scenarios)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><title>ECG Verification Report</title>");
            sb.AppendLine("<style>body{background:#0b1218;color:#e5eef5;font-family:Segoe UI,sans-serif;padding:20px} .card{background:#121d26;border:1px solid #243647;border-radius:8px;padding:16px;margin-bottom:20px} canvas{width:100%;height:190px;background:#071015;border:1px solid #1a2c38}</style></head><body>");
            sb.AppendLine("<h1>Casualties Unknown — Cardiac Grid ECG Verification</h1>");
            int idx = 0;
            foreach (var (title, trace) in scenarios)
            {
                sb.AppendLine($"<div class='card'><h3>{title}</h3><canvas id='c{idx}' width='1200' height='190'></canvas><script>");
                sb.Append("(function(){var pts=[");
                int start = Math.Max(0, trace.Count - 2000);
                for (int i = start; i < trace.Count; i++)
                {
                    if (i > start) sb.Append(',');
                    sb.Append(trace[i].II.ToString("F4", CultureInfo.InvariantCulture));
                }
                sb.AppendLine($"];var c=document.getElementById('c{idx}'),x=c.getContext('2d');x.strokeStyle='#153028';x.lineWidth=1;for(var gx=0;gx<c.width;gx+=25){{x.beginPath();x.moveTo(gx,0);x.lineTo(gx,c.height);x.stroke();}}for(var gy=0;gy<c.height;gy+=25){{x.beginPath();x.moveTo(0,gy);x.lineTo(c.width,gy);x.stroke();}}x.strokeStyle='#39ff88';x.lineWidth=1.8;x.beginPath();for(var i=0;i<pts.length;i++){{var px=i*(c.width/pts.length);var py=c.height*0.58-pts[i]*75;if(i===0)x.moveTo(px,py);else x.lineTo(px,py);}}x.stroke();}})();</script></div>");
                idx++;
            }
            sb.AppendLine("</body></html>");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }
}
