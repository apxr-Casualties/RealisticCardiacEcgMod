using System;

namespace CardiacSimCore
{
    /// <summary>
    /// 2D frontal-plane cellular electrophysiology simulator.
    /// Steps at 1 ms resolution (dt = 1 ms), computes 6 frontal ECG leads via spatial dipole projection,
    /// tracks Amiodarone / Lidocaine pharmacokinetics, models focal/global ischemia,
    /// supports energy-based defibrillation & synchronized cardioversion, and outputs LV/RV stroke volumes.
    /// </summary>
    public sealed class CardiacGridSimulator
    {
        public const float StepMs = 1.0f;
        public const float StepSec = 0.001f;

        private const byte StateResting = 0;
        private const byte StateUpstroke = 1;
        private const byte StateRefractory = 2;

        public readonly CardiacAnatomy Anatomy;

        // Per-cell state arrays (compact SoA)
        private readonly byte[] _state;
        private readonly float[] _phaseTimerMs;
        private readonly float[] _activeApdMs;
        private readonly float[] _excitationDrive;
        private readonly float[] _vm;
        private readonly float[] _nextVm;
        private readonly float[] _calcium;
        private readonly float[] _ischemia;
        private readonly float[] _stunTimerMs;
        private readonly float[] _diastoleMs;
        private readonly float[] _fastRateMemory;

        // Environment & pharmacology
        public CardiacEnvironmentInput Environment;
        public float AmiodaroneConcentration { get; set; }     // mg/L (therapeutic ~1.0 - 2.5)
        public float AmiodaronePendingPool { get; private set; }
        public float AmiodaroneHalfLifeSec { get; set; } = 300f; // 5 minutes game time

        public float LidocaineConcentration { get; set; }      // mg/L (therapeutic ~1.5 - 5.0)
        public float LidocainePendingPool { get; private set; }
        public float LidocaineHalfLifeSec { get; set; } = 120f;  // 2 minutes game time

        public float NitroprussideConcentration { get; set; }    // Potent dose-dependent arterial/venous vasodilator
        public float NitroprussidePendingPool { get; private set; }
        public float NitroprussideHalfLifeSec { get; set; } = 50f;

        public float VasopressinConcentration { get; set; }      // ADH: pure dose-dependent V1 vasoconstrictor
        public float VasopressinPendingPool { get; private set; }
        public float VasopressinHalfLifeSec { get; set; } = 120f;

        public float DopamineConcentration { get; set; }         // Dose-staged: beta1 inotropy/HR -> high-dose alpha1 vasoconstriction
        public float DopaminePendingPool { get; private set; }
        public float DopamineHalfLifeSec { get; set; } = 75f;

        // Defibrillation state
        private float _shockArtifactTimerSec;
        private float _shockArtifactPolarity;
        private float _lastShockEnergyJoules;

        // SA node & ectopic pacing
        private float _saClockMs;
        private float _avEscapeClockMs;
        private float _ectopicClockMs;
        private float _vtLoopClockMs;
        private float _vfBreakupClockMs;
        private readonly Random _rng;

        // ECG Dipole & Monitor Filtering
        private float _hpStateI;
        private float _hpStateII;
        private float _lpStateI;
        private float _lpStateII;
        private float _prevRawI;
        private float _prevRawII;
        private float _normScale = 1f / 3400f;

        // High-resolution ring buffer (500 Hz = every 2 ms, stores 12 seconds = 6000 samples)
        public const int SampleRateHz = 500;
        public const int RingCapacity = 6000;
        private readonly FrontalEcgSample[] _ringBuffer = new FrontalEcgSample[RingCapacity];
        private readonly float[] _abpRingBuffer = new float[RingCapacity];
        private int _ringWritePos;
        private int _abpValidSamples;
        private int _stepCounter;

        // Real-time beat-by-beat LV ejection & Windkessel arterial hemodynamics
        private float _lvTensionSmooth;
        private float _prevLvTensionSmooth;
        private float _lvTensionFloor;
        private float _lvDiastolicFillFrac = 1.0f;
        private float _beatStartFillFrac = 1.0f;
        private bool _lvValveOpen;
        private float _currentBeatEjectedMl;
        private float _dicroticTimerSec;
        private float _cprPulseElapsedSec;
        private float _cprPulseDurationSec;
        private float _cprPulseTotalMl;
        private float _arterialPressureMmHg = 92f;
        private float _systolicBp5s = 120f;
        private float _diastolicBp5s = 80f;
        private float _map5s = 93.3f;
        private float _effectiveVascularTone = 1.0f;
        private float _effectiveVesselDiameter = 1.0f;

        public float InstantLvFlowMlPerSec { get; private set; }
        public float LastBeatLvStrokeVolumeMl { get; private set; } = 75f;
        public long CompletedBeatCount { get; private set; }
        public float CurrentArterialPressureMmHg => _arterialPressureMmHg;
        public float SystolicBp5s => _systolicBp5s;
        public float DiastolicBp5s => _diastolicBp5s;
        public float MeanArterialPressure5s => _map5s;
        public float EffectiveVascularTone => _effectiveVascularTone;
        public float EffectiveVesselDiameter => _effectiveVesselDiameter;

        // Mechanical ejection & beat metrics
        private float _lvTensionSysPeak;
        private float _lvTensionDiaTrough = 1f;
        private float _rvTensionSysPeak;
        private float _rvTensionDiaTrough = 1f;
        private float _lvInstantTension;
        private float _rvInstantTension;
        private float _lvStrokeVolumeMl = 70f;
        private float _rvStrokeVolumeMl = 70f;
        private float _lvEjectionFraction = 0.60f;
        private float _rvEjectionFraction = 0.60f;
        private float _beatWindowTimerMs;
        private float _ventActivePeakInWindow;
        private float _ventActiveTroughInWindow = 1f;

        // R-peak & rhythm detection
        private float _lastEcgII;
        private float _prevEcgII;
        private float _rPeakLockoutMs;
        private float _lastRPeakTimeSec = -10f;
        private float _measuredRrMs = 833.3f;
        private int _recentVentricularBeats;
        private float _beatCountWindowTimerSec;
        private int _vfConfirmWindows;
        private float _vtAutoRevertTimerSec;

        public float LastEctopicBeatTimeSec { get; private set; } = -10f;

        public float SimulationTimeSec { get; private set; }
        public FrontalEcgSample LatestSample { get; private set; }
        public float MeasuredHeartRateBpm { get; private set; } = 72f;
        public float VentricularSynchrony { get; private set; } = 1f;
        public float VentricularActiveFraction { get; private set; }
        public float MyocardialAtpReserve { get; private set; } = 1f;
        public RhythmType CurrentRhythm { get; private set; } = RhythmType.NormalSinus;

        public string GetRegionPhaseDebug()
        {
            int sa = 0, atr = 0, av = 0, pk = 0, sep = 0, rv = 0, lv = 0;
            float lvVmSum = 0f, lvEpiVm = 0f, lvEndoVm = 0f;
            int lvEpiN = 0, lvEndoN = 0;
            for (int i = 0; i < Anatomy.CellCount; i++)
            {
                bool act = _state[i] != StateResting;
                switch (Anatomy.Regions[i])
                {
                    case HeartRegion.SANode: if (act) sa++; break;
                    case HeartRegion.RightAtrium:
                    case HeartRegion.LeftAtrium: if (act) atr++; break;
                    case HeartRegion.AVNode: if (act) av++; break;
                    case HeartRegion.Purkinje: if (act) pk++; break;
                    case HeartRegion.Septum: if (act) sep++; break;
                    case HeartRegion.RightVentricle: if (act) rv++; break;
                    case HeartRegion.LeftVentricle:
                        if (act) lv++;
                        lvVmSum += _vm[i];
                        if (Anatomy.TransmuralDepth[i] > 0.7f) { lvEpiVm += _vm[i]; lvEpiN++; }
                        else if (Anatomy.TransmuralDepth[i] < 0.35f) { lvEndoVm += _vm[i]; lvEndoN++; }
                        break;
                }
            }
            return $"SA={sa} Atr={atr} AV={av} PK={pk} Sep={sep} RV={rv} LV={lv} | LV_endo={(lvEndoN>0?lvEndoVm/lvEndoN:0):F0} LV_epi={(lvEpiN>0?lvEpiVm/lvEpiN:0):F0}";
        }

        public CardiacGridSimulator(int seed = 20261118)
        {
            Anatomy = CardiacAnatomy.CreateDefault();
            int n = Anatomy.CellCount;

            _state = new byte[n];
            _phaseTimerMs = new float[n];
            _activeApdMs = new float[n];
            _excitationDrive = new float[n];
            _vm = new float[n];
            _nextVm = new float[n];
            _calcium = new float[n];
            _ischemia = new float[n];
            _stunTimerMs = new float[n];
            _diastoleMs = new float[n];
            _fastRateMemory = new float[n];

            _rng = new Random(seed);
            Environment = CardiacEnvironmentInput.Normal();

            ResetToSinus();
        }

        public void ResetToSinus()
        {
            ResetToSinus(clearDrugs: false, resetEnvironment: false, targetBpm: Environment.TargetSinusBpm);
        }

        public void ResetToSinus(bool clearDrugs, bool resetEnvironment = true, float targetBpm = 72f)
        {
            if (clearDrugs)
            {
                AmiodaroneConcentration = 0f;
                AmiodaronePendingPool = 0f;
                LidocaineConcentration = 0f;
                LidocainePendingPool = 0f;
                NitroprussideConcentration = 0f;
                NitroprussidePendingPool = 0f;
                VasopressinConcentration = 0f;
                VasopressinPendingPool = 0f;
                DopamineConcentration = 0f;
                DopaminePendingPool = 0f;
            }

            if (resetEnvironment)
            {
                Environment = CardiacEnvironmentInput.Normal();
                Environment.TargetSinusBpm = targetBpm > 1f ? targetBpm : 72f;
            }

            for (int i = 0; i < Anatomy.CellCount; i++)
            {
                _state[i] = StateResting;
                _phaseTimerMs[i] = 0f;
                _activeApdMs[i] = Anatomy.BaseApdMs[i];
                _excitationDrive[i] = 0f;
                _vm[i] = -85f;
                _nextVm[i] = -85f;
                _calcium[i] = 0f;
                _ischemia[i] = 0f;
                _stunTimerMs[i] = 0f;
                _diastoleMs[i] = 400f;
                _fastRateMemory[i] = 0f;
            }

            _saClockMs = 20f;
            _avEscapeClockMs = 0f;
            _ectopicClockMs = 0f;
            _vtLoopClockMs = 0f;
            _vfBreakupClockMs = 0f;
            _shockArtifactTimerSec = 0f;
            _hpStateI = 0f;
            _hpStateII = 0f;
            _lpStateI = 0f;
            _lpStateII = 0f;
            _prevRawI = 0f;
            _prevRawII = 0f;
            _lastEcgII = 0f;
            _prevEcgII = 0f;
            _rPeakLockoutMs = 0f;
            _lvTensionSysPeak = 0f;
            _lvTensionDiaTrough = 0f;
            _rvTensionSysPeak = 0f;
            _rvTensionDiaTrough = 0f;
            _ventActivePeakInWindow = 0f;
            _ventActiveTroughInWindow = 0f;
            _beatWindowTimerMs = 0f;
            _vfConfirmWindows = 0;
            _vtAutoRevertTimerSec = 0f;

            Array.Clear(_ringBuffer, 0, RingCapacity);
            for (int k = 0; k < RingCapacity; k++)
                _abpRingBuffer[k] = 92f;
            _ringWritePos = 0;
            _abpValidSamples = 0;

            _lvTensionSmooth = 0f;
            _prevLvTensionSmooth = 0f;
            _lvTensionFloor = 0f;
            _lvDiastolicFillFrac = 1.0f;
            _beatStartFillFrac = 1.0f;
            _lvValveOpen = false;
            _currentBeatEjectedMl = 0f;
            _dicroticTimerSec = 0f;
            _cprPulseElapsedSec = 0f;
            _cprPulseDurationSec = 0f;
            _cprPulseTotalMl = 0f;
            _arterialPressureMmHg = 92f;
            _systolicBp5s = 120f;
            _diastolicBp5s = 80f;
            _map5s = 93.3f;
            _effectiveVascularTone = 1.0f;
            _effectiveVesselDiameter = 1.0f;
            InstantLvFlowMlPerSec = 0f;
            LastBeatLvStrokeVolumeMl = 75f;

            _lvStrokeVolumeMl = 75f;
            _rvStrokeVolumeMl = 75f;
            _lvEjectionFraction = 0.60f;
            _rvEjectionFraction = 0.60f;
            VentricularSynchrony = 1f;
            MyocardialAtpReserve = 1f;
            LastEctopicBeatTimeSec = -10f;
            MeasuredHeartRateBpm = Environment.TargetSinusBpm;
            CurrentRhythm = MeasuredHeartRateBpm > 100f
                ? RhythmType.SinusTachycardia
                : (MeasuredHeartRateBpm < 60f ? RhythmType.SinusBradycardia : RhythmType.NormalSinus);
        }

        /// <summary>
        /// Administer intravenous Amiodarone (e.g. 150 mg or 300 mg bolus).
        /// </summary>
        public void AdministerAmiodarone(float doseMg)
        {
            if (doseMg <= 0f) return;
            // 150 mg IV bolus typically yields ~1.2 - 1.8 mg/L effective plasma/myocardial loading
            AmiodaronePendingPool += doseMg / 100f;
        }

        /// <summary>
        /// Administer intravenous Lidocaine (e.g. 75 mg or 100 mg bolus).
        /// </summary>
        public void AdministerLidocaine(float doseMg)
        {
            if (doseMg <= 0f) return;
            // 100 mg IV bolus yields ~2.5 - 3.5 mg/L peak plasma level
            LidocainePendingPool += doseMg / 33f;
        }

        /// <summary>
        /// Administer intravenous Sodium Nitroprusside (rapid dose-dependent vasodilator, t1/2 ~ 50s).
        /// Standard 20 mL bolus = 2.0 dose units.
        /// </summary>
        public void AdministerNitroprusside(float doseUnits)
        {
            if (doseUnits <= 0f) return;
            NitroprussidePendingPool += doseUnits;
        }

        /// <summary>
        /// Administer intravenous Vasopressin / ADH (pure dose-dependent V1 vasoconstrictor, t1/2 ~ 120s).
        /// Standard 20 mL bolus = 2.0 dose units.
        /// </summary>
        public void AdministerVasopressin(float doseUnits)
        {
            if (doseUnits <= 0f) return;
            VasopressinPendingPool += doseUnits;
        }

        /// <summary>
        /// Administer intravenous Dopamine (dose-staged: low/mod beta1 inotropy &amp; HR -> high alpha1 vasoconstriction, t1/2 ~ 75s).
        /// Standard 10 mL = 1.0 unit (beta1), 20+ mL = 2.0+ units (beta1 + strong alpha1 pressor).
        /// </summary>
        public void AdministerDopamine(float doseUnits)
        {
            if (doseUnits <= 0f) return;
            DopaminePendingPool += doseUnits;
        }

        /// <summary>
        /// Inject a mechanical CPR chest-compression arterial pulse (e.g. AutoPump every 0.3s).
        /// </summary>
        public void InjectCprPulse(float impulseMl = 26f)
        {
            if (impulseMl <= 0f) return;
            _cprPulseElapsedSec = 0f;
            _cprPulseDurationSec = 0.13f;
            _cprPulseTotalMl = Clamp(impulseMl, 6f, 90f);
        }

        public float GetInstantaneousLvEjectionFlowMlPerSec() => InstantLvFlowMlPerSec;
        public float GetLastBeatLvStrokeVolumeMl() => LastBeatLvStrokeVolumeMl;

        /// <summary>
        /// Restore persisted pharmacology, ATP reserve, and rhythm state when loading a save.
        /// </summary>
        public void RestoreSavedState(
            float amioConc,
            float amioPending,
            float lidoConc,
            float lidoPending,
            float atpReserve,
            RhythmType rhythm,
            float targetBpm)
        {
            RestoreSavedState(amioConc, amioPending, lidoConc, lidoPending, 0f, 0f, 0f, 0f, 0f, 0f, atpReserve, rhythm, targetBpm, 95f);
        }

        public void RestoreSavedState(
            float amioConc,
            float amioPending,
            float lidoConc,
            float lidoPending,
            float nitroConc,
            float nitroPending,
            float vasoConc,
            float vasoPending,
            float dopaConc,
            float dopaPending,
            float atpReserve,
            RhythmType rhythm,
            float targetBpm,
            float arterialPressureMmHg)
        {
            ResetToSinus(clearDrugs: true, resetEnvironment: false, targetBpm: Clamp(targetBpm, 35f, 200f));
            AmiodaroneConcentration = Math.Max(0f, amioConc);
            AmiodaronePendingPool = Math.Max(0f, amioPending);
            LidocaineConcentration = Math.Max(0f, lidoConc);
            LidocainePendingPool = Math.Max(0f, lidoPending);
            NitroprussideConcentration = Math.Max(0f, nitroConc);
            NitroprussidePendingPool = Math.Max(0f, nitroPending);
            VasopressinConcentration = Math.Max(0f, vasoConc);
            VasopressinPendingPool = Math.Max(0f, vasoPending);
            DopamineConcentration = Math.Max(0f, dopaConc);
            DopaminePendingPool = Math.Max(0f, dopaPending);
            MyocardialAtpReserve = Clamp(atpReserve, 0f, 1f);
            _arterialPressureMmHg = Clamp(arterialPressureMmHg, 0f, 250f);
            for (int k = 0; k < RingCapacity; k++)
                _abpRingBuffer[k] = _arterialPressureMmHg;
            _abpValidSamples = 1;
            _systolicBp5s = _arterialPressureMmHg;
            _diastolicBp5s = _arterialPressureMmHg;
            _map5s = _arterialPressureMmHg;

            if (rhythm == RhythmType.Asystole || MyocardialAtpReserve <= 0.02f)
            {
                CurrentRhythm = RhythmType.Asystole;
                MeasuredHeartRateBpm = 0f;
                VentricularSynchrony = 0f;
                _lvStrokeVolumeMl = 0f;
                _rvStrokeVolumeMl = 0f;
            }
            else if (rhythm == RhythmType.VentricularFibrillation)
            {
                InduceVentricularFibrillation();
            }
            else if (rhythm == RhythmType.VentricularTachycardia)
            {
                InduceVentricularTachycardia();
            }

            // Warm up 1.0s into the ring buffer so restored waveforms display immediately
            for (int k = 0; k < 1000; k++)
                StepOneMillisecond();
        }

        /// <summary>
        /// Deliver a defibrillation shock with specified energy in Joules (typical biphasic: 50 - 200 J).
        /// Resets cells within the electric field to synchronized depolarization + absolute refractory period.
        /// Low energy may leave uncaptured pockets allowing VF reentry to persist.
        /// </summary>
        public void Defibrillate(float energyJoules)
        {
            if (energyJoules <= 0f) return;

            _lastShockEnergyJoules = energyJoules;
            _shockArtifactTimerSec = 0.14f;
            _shockArtifactPolarity = 1f;

            // Coverage model for biphasic defibrillation:
            // 30 J -> ~0.36 base (fails to extinguish VF), 100 J -> ~0.78, 180 J -> ~0.98 (succeeds)
            float captureCurve = 1f - (float)Math.Exp(-energyJoules / 70f);
            float baseField = Clamp(captureCurve * 1.06f, 0.05f, 1.0f);

            // Detect whether an organized beat is currently in T-wave vulnerable repolarization window
            int ventRefractoryMid = 0;
            int ventTotal = Anatomy.VentricularCells.Length;
            for (int k = 0; k < ventTotal; k++)
            {
                int idx = Anatomy.VentricularCells[k];
                if (_state[idx] == StateRefractory)
                {
                    float u = _phaseTimerMs[idx] / Math.Max(1f, _activeApdMs[idx]);
                    if (u > 0.68f && u < 0.94f) ventRefractoryMid++;
                }
            }
            float vulnerableFrac = (float)ventRefractoryMid / Math.Max(1, ventTotal);
            bool inVulnerableWindow = CurrentRhythm != RhythmType.VentricularFibrillation &&
                                      CurrentRhythm != RhythmType.VentricularTachycardia &&
                                      vulnerableFrac > 0.25f &&
                                      energyJoules >= 15f && energyJoules <= 120f;

            float amioExt = 1f + 0.30f * Clamp(AmiodaroneConcentration, 0f, 3f);

            // Capture fraction check: low energy (e.g. 30 J -> ~0.36 baseField) fails to capture enough myocardium to terminate VF/VT
            int capturedCount = 0;
            for (int i = 0; i < Anatomy.CellCount; i++)
            {
                // Spatial field gradient across chest paddles (apex-anterior to right-clavicle)
                float gx = Anatomy.GridX[i] / CardiacAnatomy.GridSize;
                float gy = Anatomy.GridY[i] / CardiacAnatomy.GridSize;
                float paddleDist = Math.Abs((gx - 0.5f) * 0.45f - (gy - 0.5f) * 0.45f);
                float localField = baseField - paddleDist * 0.18f + ((float)_rng.NextDouble() - 0.5f) * 0.12f;

                if (inVulnerableWindow)
                {
                    // R-on-T heterogeneous capture can trigger reentry!
                    localField *= 0.65f;
                }

                if (localField >= 0.42f)
                {
                    capturedCount++;
                    // Captured by electric shock: forced simultaneous depolarization -> absolute refractory reset
                    _state[i] = StateRefractory;
                    float apd = Anatomy.BaseApdMs[i] * amioExt;
                    _activeApdMs[i] = apd;
                    // Place captured cells at start of plateau/refractory so all wavefronts extinguish together
                    _phaseTimerMs[i] = apd * 0.12f;
                    _excitationDrive[i] = 0f;
                    _vm[i] = 12f;
                    _nextVm[i] = 12f;
                    _fastRateMemory[i] = 0f;

                    // Brief myocardial stunning proportional to excess energy (> 200 J)
                    if (energyJoules > 180f)
                    {
                        _stunTimerMs[i] = Math.Min(1500f, (energyJoules - 180f) * 4f);
                    }
                }
            }

            float capturedFrac = (float)capturedCount / Math.Max(1, Anatomy.CellCount);
            if (CurrentRhythm == RhythmType.Asystole || MyocardialAtpReserve <= 0.05f)
            {
                // Asystole / ATP-depleted myocardium is non-shockable; keep asystole
                CurrentRhythm = RhythmType.Asystole;
                MeasuredHeartRateBpm = 0f;
                VentricularSynchrony = 0f;
            }
            else if (inVulnerableWindow)
            {
                InduceVentricularFibrillation();
            }
            else if (capturedFrac >= 0.80f)
            {
                // Successful defibrillation/cardioversion: resets all rotors into post-shock sinus pause
                CurrentRhythm = RhythmType.NormalSinus;
                VentricularSynchrony = 0.85f;
                MeasuredHeartRateBpm = Clamp(Environment.TargetSinusBpm, 40f, 160f);
                _vtLoopClockMs = 0f;
                _vfConfirmWindows = 0;
                _vtAutoRevertTimerSec = 0f;
                _vfBreakupClockMs = 0f;
            }
            else
            {
                // Low-energy shock failed to capture critical mass -> surviving pockets re-seed VT/VF rotors
                for (int i = 0; i < Anatomy.CellCount; i++)
                    _fastRateMemory[i] = Math.Max(_fastRateMemory[i], 0.88f);
            }

            // SA node pauses briefly post-shock (~450 ms) before resuming intrinsic rhythm
            _saClockMs = Math.Max(_saClockMs, 450f);
        }

        /// <summary>
        /// Induce a Premature Ventricular Contraction (PVC) at the LV ischemic border zone (useful for tests/pathology).
        /// </summary>
        public void TriggerPrematureVentricularBeat()
        {
            LastEctopicBeatTimeSec = SimulationTimeSec;
            TriggerFocalZone(47f, 47f, 3.2f);
        }

        /// <summary>
        /// Induce Ventricular Tachycardia (reentrant ventricular rotor / fast ectopic focus).
        /// </summary>
        public void InduceVentricularTachycardia()
        {
            // Seed a macro-reentry loop around LV + Septum (center ~39, 44):
            // Upstroke starts at lateral LV (angle ~ 0), propagates down through apex -> septum -> base!
            for (int k = 0; k < Anatomy.VentricularCells.Length; k++)
            {
                int idx = Anatomy.VentricularCells[k];
                if (Anatomy.Regions[idx] == HeartRegion.Purkinje)
                {
                    _state[idx] = StateResting;
                    _phaseTimerMs[idx] = 0f;
                    _excitationDrive[idx] = 0f;
                    _vm[idx] = -85f;
                    continue;
                }
                if (Anatomy.Regions[idx] == HeartRegion.RightVentricle)
                {
                    _state[idx] = StateResting;
                    _phaseTimerMs[idx] = 0f;
                    _excitationDrive[idx] = 0f;
                    _vm[idx] = -85f;
                    _fastRateMemory[idx] = 0.65f;
                    continue;
                }

                float x = Anatomy.GridX[idx];
                float y = Anatomy.GridY[idx];
                _diastoleMs[idx] = 45f;
                _fastRateMemory[idx] = 0.82f;
                float apd = 70f * (1f + 0.30f * Clamp(AmiodaroneConcentration, 0f, 3f));
                _activeApdMs[idx] = apd;

                // Single monomorphic rotor centered at (43, 45) inside LV+Septum (same geometry that sustains cleanly in VF!)
                double angle = Math.Atan2(y - 45f, x - 43f);
                if (angle < 0) angle += 2.0 * Math.PI;
                double phase = angle / (2.0 * Math.PI);
                if (phase < 0.18)
                {
                    _state[idx] = StateUpstroke;
                    _phaseTimerMs[idx] = 2f;
                    _vm[idx] = 18f;
                    _excitationDrive[idx] = 0f;
                }
                else if (phase > 0.72)
                {
                    _state[idx] = StateRefractory;
                    _phaseTimerMs[idx] = (float)((0.25 + 0.65 * (1.0 - phase) / 0.28) * apd);
                    _vm[idx] = 4f;
                    _excitationDrive[idx] = 0f;
                }
                else
                {
                    _state[idx] = StateResting;
                    _phaseTimerMs[idx] = 0f;
                    _vm[idx] = -85f;
                    _excitationDrive[idx] = 0f;
                }
            }
            VentricularSynchrony = 0.55f;
            MeasuredHeartRateBpm = 205f;
            _lastRPeakTimeSec = SimulationTimeSec;
            _beatWindowTimerMs = 0f;
            _vfConfirmWindows = 0;
            _vtAutoRevertTimerSec = 0f;
            CurrentRhythm = RhythmType.VentricularTachycardia;
        }

        /// <summary>
        /// Induce chaotic multi-wavelet Ventricular Fibrillation across both ventricles.
        /// </summary>
        public void InduceVentricularFibrillation()
        {
            for (int k = 0; k < Anatomy.VentricularCells.Length; k++)
            {
                int idx = Anatomy.VentricularCells[k];
                if (Anatomy.Regions[idx] == HeartRegion.Purkinje)
                {
                    _state[idx] = StateResting;
                    _phaseTimerMs[idx] = 0f;
                    _vm[idx] = -85f;
                    _excitationDrive[idx] = 0f;
                    continue;
                }
                float x = Anatomy.GridX[idx];
                float y = Anatomy.GridY[idx];

                // Counter-rotating rotors anchored right inside thick LV wall (43,45) and RV/Septum (27,43)
                double angle = (x >= 33f)
                    ? Math.Atan2(y - 45f, x - 43f)
                    : -Math.Atan2(y - 43f, x - 27f);
                if (angle < 0) angle += 2.0 * Math.PI;
                double phaseNorm = (angle / (2.0 * Math.PI) + _rng.NextDouble() * 0.08) % 1.0;

                float apd = 60f;
                _activeApdMs[idx] = apd;
                _diastoleMs[idx] = 25f;
                _fastRateMemory[idx] = 0.92f;
                if (phaseNorm < 0.18)
                {
                    _state[idx] = StateUpstroke;
                    _phaseTimerMs[idx] = 2f;
                    _vm[idx] = 18f;
                    _excitationDrive[idx] = 0f;
                }
                else if (phaseNorm > 0.68)
                {
                    _state[idx] = StateRefractory;
                    _phaseTimerMs[idx] = (float)((0.25 + 0.65 * (1.0 - phaseNorm) / 0.32) * apd);
                    _vm[idx] = -6f;
                    _excitationDrive[idx] = 0f;
                }
                else
                {
                    _state[idx] = StateResting;
                    _phaseTimerMs[idx] = 0f;
                    _vm[idx] = -85f;
                    _excitationDrive[idx] = 0f;
                }
            }
            VentricularSynchrony = 0.15f;
            _lastRPeakTimeSec = SimulationTimeSec;
            _beatWindowTimerMs = 0f;
            _vfConfirmWindows = 2;
            _vtAutoRevertTimerSec = 0f;
            CurrentRhythm = RhythmType.VentricularFibrillation;
        }

        /// <summary>
        /// Left Ventricular Stroke Volume in mL/beat (normal ~70 mL; drops near 0 in VF/asystole).
        /// </summary>
        public float GetLeftVentricularEjectionVolumeMl() => _lvStrokeVolumeMl;

        /// <summary>
        /// Right Ventricular Stroke Volume in mL/beat (normal ~70 mL; reduced in RV ischemia / VF).
        /// </summary>
        public float GetRightVentricularEjectionVolumeMl() => _rvStrokeVolumeMl;

        /// <summary>
        /// Left Ventricular Cardiac Output in L/min.
        /// </summary>
        public float GetLeftVentricularCardiacOutputLpm() => (_lvStrokeVolumeMl * MeasuredHeartRateBpm) / 1000f;

        /// <summary>
        /// Right Ventricular Cardiac Output in L/min.
        /// </summary>
        public float GetRightVentricularCardiacOutputLpm() => (_rvStrokeVolumeMl * MeasuredHeartRateBpm) / 1000f;

        /// <summary>
        /// Left Ventricular Ejection Fraction [0..1] (normal ~0.60).
        /// </summary>
        public float GetLeftVentricularEjectionFraction() => _lvEjectionFraction;

        /// <summary>
        /// Right Ventricular Ejection Fraction [0..1] (normal ~0.60).
        /// </summary>
        public float GetRightVentricularEjectionFraction() => _rvEjectionFraction;

        /// <summary>
        /// Advance simulation by deltaSeconds (e.g. Time.deltaTime), internally subdivided into 1 ms steps.
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (deltaSeconds <= 0f) return;

            int steps = (int)Math.Round(deltaSeconds * 1000f);
            if (steps < 1) steps = 1;
            // Safety cap per call (allows 20x speed easily when worker batches chunks)
            if (steps > 20000) steps = 20000;

            for (int s = 0; s < steps; s++)
            {
                StepOneMillisecond();
            }
        }

        public void StepOneMillisecond()
        {
            SimulationTimeSec += StepSec;
            _stepCounter++;

            // 1. Update Pharmacokinetics (Amiodarone 5-min half-life, Lidocaine 2-min half-life)
            UpdatePharmacokinetics();

            // 2. Compute global modulators
            float amio = Clamp(AmiodaroneConcentration, 0f, 4f);
            float lido = Clamp(LidocaineConcentration, 0f, 8f);
            float dopa = Math.Max(0f, DopamineConcentration);
            float dopaBeta1 = Clamp(dopa * 0.50f, 0f, 1.15f);
            float dopaEctopy = Clamp((dopa - 2.4f) * 0.22f, 0f, 0.55f);
            float tempFactor = Clamp(1f + (37f - Environment.TemperatureC) * 0.045f, 0.75f, 1.8f); // Hypothermia slows conduction & prolongs APD
            float adren = Math.Max(0f, Environment.Adrenaline); // Uncapped total adrenaline (1.0 = max endogenous 100)
            float caOverload = Clamp((adren - 1.0f) * 0.35f + dopaEctopy * 0.65f, 0f, 0.85f);

            // Amiodarone slows SA rate slightly; uncapped total adrenaline + Dopamine beta1 accelerate SA rate
            float adrenRateMult = 1f + 0.40f * Math.Min(adren, 1.5f) + 0.16f * Math.Max(0f, Math.Min(adren, 4.0f) - 1.5f) + 0.24f * dopaBeta1;
            float effectiveSaBpm = Environment.TargetSinusBpm * adrenRateMult * (1f - 0.14f * Clamp(amio / 2f, 0f, 1.5f)) / tempFactor;
            effectiveSaBpm = Clamp(effectiveSaBpm, 0f, 235f);

            float amioApdMult = 1f + 0.24f * amio;
            float amioAvSlowMult = 1f / (1f + 0.22f * amio);
            float avAdrenConductionMult = 1f + 0.55f * (float)Math.Tanh((adren + 0.35f * dopaBeta1) * 0.85);
            float adrenConductionMult = 1f + 0.32f * (float)Math.Tanh((adren + 0.25f * dopaBeta1) * 0.75);
            float rateApdAdapt = Clamp((float)Math.Sqrt(72f / Math.Max(45f, MeasuredHeartRateBpm)), 0.70f, 1.15f);

            // 3. Myocardial ATP reserve & VF -> Fine VF -> Asystole AND CPR (BP > 60 && SpO2 > 50) -> Asystole -> Fine VF -> Coarse VF
            float maxIschEnv = Math.Max(Environment.GlobalIschemia, Environment.FocalTransmuralIschemia);
            if (CurrentRhythm == RhythmType.VentricularFibrillation)
            {
                if (Environment.AllowAtpRecovery)
                {
                    // Adequate CPR perfusion (BP > 60, SpO2 > 50): Fine VF builds energy back up to Coarse VF
                    MyocardialAtpReserve = Math.Min(1f, MyocardialAtpReserve + 0.025f * StepSec);
                }
                else
                {
                    // Untreated VF has zero coronary perfusion -> depletes ATP over ~60-75 game seconds into Asystole
                    float drainRate = (1f / 75f) + 0.012f * maxIschEnv;
                    MyocardialAtpReserve = Math.Max(0f, MyocardialAtpReserve - drainRate * StepSec);
                    if (MyocardialAtpReserve <= 0.02f)
                    {
                        CurrentRhythm = RhythmType.Asystole;
                        MeasuredHeartRateBpm = 0f;
                        VentricularSynchrony = 0f;
                        _lvStrokeVolumeMl = 0f;
                        _rvStrokeVolumeMl = 0f;
                        for (int i = 0; i < Anatomy.CellCount; i++)
                        {
                            _state[i] = StateResting;
                            _phaseTimerMs[i] = 0f;
                            _excitationDrive[i] = 0f;
                            _fastRateMemory[i] = 0f;
                            _vm[i] = -85f;
                            _nextVm[i] = -85f;
                            _calcium[i] = 0f;
                        }
                    }
                }
            }
            else if (CurrentRhythm == RhythmType.Asystole)
            {
                if (Environment.AllowAtpRecovery)
                {
                    // Adequate CPR perfusion restores myocardial ATP reserve; once >= 0.15, restarts as Fine VF
                    MyocardialAtpReserve = Math.Min(1f, MyocardialAtpReserve + 0.025f * StepSec);
                    if (MyocardialAtpReserve >= 0.15f)
                    {
                        InduceVentricularFibrillation();
                    }
                }
                else
                {
                    // Asystole without CPR stays depleted until Heal / TryLastStand
                    MyocardialAtpReserve = Math.Max(0f, MyocardialAtpReserve - 0.01f * StepSec);
                }
            }
            else
            {
                if (Environment.AllowAtpRecovery)
                    MyocardialAtpReserve = Math.Min(1f, MyocardialAtpReserve + 0.03f * StepSec);
                else if (maxIschEnv > 0.80f)
                    MyocardialAtpReserve = Math.Max(0.15f, MyocardialAtpReserve - 0.008f * (maxIschEnv - 0.80f) * 5f * StepSec);
                else if (maxIschEnv < 0.50f)
                    MyocardialAtpReserve = Math.Min(1f, MyocardialAtpReserve + 0.03f * StepSec);
            }

            // 4. SA Node Pacing (with AV junctional escape backup if SA stops, disabled in Asystole with depleted ATP)
            if (CurrentRhythm != RhythmType.Asystole && effectiveSaBpm > 1f && MyocardialAtpReserve > 0.05f)
            {
                _saClockMs -= StepMs;
                if (_saClockMs <= 0f)
                {
                    float cycleMs = 60000f / effectiveSaBpm;
                    _saClockMs = cycleMs;
                    _avEscapeClockMs = 0f;
                    TriggerSaNode();
                }
            }
            else if (CurrentRhythm != RhythmType.Asystole && MyocardialAtpReserve > 0.15f)
            {
                _avEscapeClockMs += StepMs;
                if (_avEscapeClockMs >= 1500f) // ~40 bpm junctional escape if SA arrests
                {
                    _avEscapeClockMs = 0f;
                    TriggerAvEscape();
                }
            }

            // 4a. Graded ischemia & catecholamine calcium-overload ectopy:
            //     Moderate stress -> Isolated PVCs -> Couplets / VT -> Severe R-on-T / VT degeneration -> VF
            float maxFocalIsch = Math.Max(Environment.FocalTransmuralIschemia, Environment.GlobalIschemia);
            float effectiveArrhythmiaStress = Clamp(
                Math.Max(maxFocalIsch, 0.55f * caOverload + (adren > 1.0f ? 0.25f * Math.Min(adren - 1.0f, 1.8f) : 0f)),
                0f,
                1.25f);
            if (CurrentRhythm != RhythmType.Asystole &&
                CurrentRhythm != RhythmType.VentricularFibrillation &&
                CurrentRhythm != RhythmType.VentricularTachycardia &&
                effectiveArrhythmiaStress > 0.34f)
            {
                // Lidocaine strongly suppresses ischemic / DAD automaticity & ectopic firing
                float lidoSuppression = 1f / (1f + 1.8f * lido);
                float ectopicRatePerSec = (effectiveArrhythmiaStress - 0.30f) * 2.15f * lidoSuppression;
                _ectopicClockMs += StepMs * ectopicRatePerSec * 0.001f;
                if (_ectopicClockMs >= 1.0f)
                {
                    // Under moderate stress (<0.74), wait until late repolarization/early diastole so beat becomes clean PVC instead of accidental R-on-T VF
                    if (effectiveArrhythmiaStress < 0.74f && VentricularActiveFraction > 0.14f)
                    {
                        _ectopicClockMs = 0.99f;
                    }
                    else
                    {
                        _ectopicClockMs = 0f;
                        double roll = _rng.NextDouble();
                        float vtTriggerProb = (effectiveArrhythmiaStress >= 0.74f && lidoSuppression > 0.45f)
                            ? Clamp((effectiveArrhythmiaStress - 0.68f) * 1.15f * lidoSuppression, 0f, 0.50f)
                            : 0f;
                        if (roll < vtTriggerProb)
                        {
                            InduceVentricularTachycardia();
                        }
                        else if (roll < 0.80)
                        {
                            LastEctopicBeatTimeSec = SimulationTimeSec;
                            TriggerFocalZone(48f + (float)(_rng.NextDouble() - 0.5) * 6f, 47f + (float)(_rng.NextDouble() - 0.5) * 6f, 3.0f);
                        }
                    }
                }
            }

            // 4b. Sustaining 3D-equivalent wavelet breakup in active VF and scar-loop reentry in VT
            // (Terminated by Defibrillation >=80% capture or therapeutic Amiodarone)
            if (CurrentRhythm == RhythmType.VentricularFibrillation)
            {
                if (amio > 1.8f && _rng.NextDouble() < 0.0005 * amio)
                {
                    // High-dose Amiodarone can chemically cardiovert VF/VT by prolonging ERP beyond loop length
                    CurrentRhythm = RhythmType.NormalSinus;
                    _vfConfirmWindows = 0;
                }
                else
                {
                    _vfBreakupClockMs += StepMs;
                    if (_vfBreakupClockMs >= 65f)
                    {
                        _vfBreakupClockMs = 0f;
                        // Spawn a daughter wavelet in resting ventricular tissue if wave activity dips
                        if (VentricularActiveFraction < 0.28f)
                        {
                            float rx = 20f + (float)_rng.NextDouble() * 34f;
                            float ry = 36f + (float)_rng.NextDouble() * 20f;
                            TriggerFocalZone(rx, ry, 3.0f);
                        }
                    }
                }
            }
            else if (CurrentRhythm == RhythmType.VentricularTachycardia)
            {
                if (amio > 1.5f && _rng.NextDouble() < 0.0008 * amio)
                {
                    CurrentRhythm = RhythmType.NormalSinus;
                    _vtAutoRevertTimerSec = 0f;
                }
                else if (effectiveArrhythmiaStress > 0.78f && lido < 0.8f && _rng.NextDouble() < 0.00035 * (effectiveArrhythmiaStress - 0.74f))
                {
                    // Sustained VT degenerates into VF under severe ischemia / extreme catecholamine toxicity
                    InduceVentricularFibrillation();
                }
                else
                {
                    if (effectiveArrhythmiaStress < 0.45f || lido > 1.4f)
                    {
                        _vtAutoRevertTimerSec += StepSec;
                        if (_vtAutoRevertTimerSec >= 4.0f && _rng.NextDouble() < 0.003)
                        {
                            CurrentRhythm = RhythmType.NormalSinus;
                            _vtAutoRevertTimerSec = 0f;
                        }
                    }
                    else
                    {
                        _vtAutoRevertTimerSec = 0f;
                    }

                    _vtLoopClockMs += StepMs;
                    float vtCycleMs = Clamp(320f * (1f + 0.20f * amio) / (1f + 0.14f * Math.Min(adren, 2.5f)), 245f, 440f);
                    if (_vtLoopClockMs >= vtCycleMs)
                    {
                        _vtLoopClockMs = 0f;
                        TriggerFocalZone(49f, 48f, 3.8f);
                    }
                }
            }

            // 5. Update cell membrane potentials & propagate 8-neighbor excitation
            int n = Anatomy.CellCount;
            int[] nIdx = Anatomy.NeighborIdx;
            float[] nW = Anatomy.NeighborWeight;

            float globalIsch = Clamp(Environment.GlobalIschemia, 0f, 1f);
            float transmuralIsch = Clamp(Environment.FocalTransmuralIschemia, 0f, 1f);
            float subendoIsch = Clamp(Environment.SubendocardialIschemia, 0f, 1f);
            float rvIsch = Clamp(Environment.RightVentricularIschemia, 0f, 1f);

            int ventActiveCount = 0;
            float lvTensionSum = 0f;
            float rvTensionSum = 0f;

            for (int i = 0; i < n; i++)
            {
                HeartRegion reg = Anatomy.Regions[i];

                // Compute local cell ischemia [0..1]
                float localIsch = globalIsch * 0.85f;
                if (reg == HeartRegion.LeftVentricle)
                {
                    localIsch = Math.Max(localIsch, Anatomy.FocalLvMask[i] * transmuralIsch);
                    localIsch = Math.Max(localIsch, Anatomy.SubendoLvMask[i] * subendoIsch);
                }
                else if (reg == HeartRegion.RightVentricle)
                {
                    localIsch = Math.Max(localIsch, Anatomy.FocalRvMask[i] * rvIsch);
                }
                _ischemia[i] = localIsch;

                if (_stunTimerMs[i] > 0f)
                    _stunTimerMs[i] = Math.Max(0f, _stunTimerMs[i] - StepMs);

                byte st = _state[i];
                float vm = _vm[i];
                float apd = _activeApdMs[i];

                bool isVent = reg == HeartRegion.LeftVentricle || reg == HeartRegion.RightVentricle || reg == HeartRegion.Septum;

                if (st == StateResting)
                {
                    _diastoleMs[i] = Math.Min(1000f, _diastoleMs[i] + StepMs);
                    if (_fastRateMemory[i] > 0f && _diastoleMs[i] > 180f)
                        _fastRateMemory[i] = Math.Max(0f, _fastRateMemory[i] - StepMs / 1600f);

                    // Compute excitatory current strictly from active Phase-0/1 depolarization wavefront neighbors
                    float iDiff = 0f;
                    int baseOff = i * CardiacAnatomy.MaxNeighbors;
                    bool blockPurkinjeExit = reg != HeartRegion.Purkinje && isVent && VentricularActiveFraction > 0.03f &&
                                             (CurrentRhythm == RhythmType.VentricularFibrillation || CurrentRhythm == RhythmType.VentricularTachycardia);
                    for (int k = 0; k < CardiacAnatomy.MaxNeighbors; k++)
                    {
                        int nb = nIdx[baseOff + k];
                        if (nb >= 0 && _state[nb] == StateUpstroke)
                        {
                            HeartRegion nbReg = Anatomy.Regions[nb];
                            // Purkinje-Myocardial Junction (PMJ) protection
                            if (reg == HeartRegion.Purkinje && nbReg != HeartRegion.Purkinje && nbReg != HeartRegion.AVNode)
                                continue;
                            // Prevent ventricular VT/VF rotors from retrogradely entering AV node
                            if (reg == HeartRegion.AVNode &&
                                (CurrentRhythm == RhythmType.VentricularFibrillation || CurrentRhythm == RhythmType.VentricularTachycardia) &&
                                (nbReg == HeartRegion.Purkinje || nbReg == HeartRegion.Septum || nbReg == HeartRegion.LeftVentricle || nbReg == HeartRegion.RightVentricle))
                                continue;
                            // AV dissociation: Purkinje & AVNode cannot override actively fibrillating/reentrant myocardium
                            if (blockPurkinjeExit && (nbReg == HeartRegion.Purkinje || nbReg == HeartRegion.AVNode))
                                continue;

                            float nbVm = _vm[nb];
                            float dv = nbVm - vm;
                            if (dv > 0f)
                                iDiff += nW[baseOff + k] * (dv / 105f);
                        }
                    }

                    if (iDiff > 0.001f)
                    {
                        float condMult = ((reg == HeartRegion.AVNode) ? avAdrenConductionMult : adrenConductionMult) / tempFactor;
                        if (reg == HeartRegion.AVNode) condMult *= amioAvSlowMult;
                        // Fast-rate conduction restitution (DI < 155ms or active fast-rate rotor memory)
                        if (isVent && reg != HeartRegion.Purkinje)
                        {
                            float diFactor = _diastoleMs[i] < 155f ? (0.28f + 0.72f * (_diastoleMs[i] / 155f)) : 1f;
                            float memFactor = 1f - 0.72f * _fastRateMemory[i];
                            condMult *= Math.Min(diFactor, memFactor);
                        }
                        // Ischemia slows ventricular conduction substantially; nodal tissues only mildly slowed so ischemic pauses don't lock SA/AV
                        condMult *= (reg == HeartRegion.AVNode || reg == HeartRegion.SANode)
                            ? (1f - 0.25f * localIsch)
                            : (1f - 0.62f * localIsch);
                        // Lidocaine further slows conduction in ischemic myocardium
                        if (isVent && localIsch > 0.25f)
                            condMult *= 1f / (1f + 0.35f * lido * localIsch);

                        float drive = _excitationDrive[i] + Anatomy.BaseAlpha[i] * condMult * iDiff;
                        float threshold = 1.0f + 0.12f * Clamp(amio / 2f, 0f, 1.5f) + 0.22f * localIsch + (isVent ? 0.12f * lido * localIsch : 0f);

                        if (drive >= threshold)
                        {
                            float restit = 1.0f;
                            if (isVent && reg != HeartRegion.Purkinje)
                            {
                                if (_diastoleMs[i] < 240f)
                                    _fastRateMemory[i] = Math.Min(1f, _fastRateMemory[i] + 0.45f * (1f - _diastoleMs[i] / 240f));
                                float diRest = _diastoleMs[i] < 160f ? (0.20f + 0.80f * (_diastoleMs[i] / 160f)) : 1f;
                                float memRest = 1f - 0.72f * _fastRateMemory[i];
                                restit = Clamp(Math.Min(diRest, memRest), 0.22f, 1.0f);
                            }
                            ActivateCell(i, reg, localIsch, amioApdMult, lido, rateApdAdapt, tempFactor, restit);
                        }
                        else
                        {
                            _excitationDrive[i] = drive;
                        }
                    }
                    else if (_excitationDrive[i] > 0f)
                    {
                        _excitationDrive[i] = Math.Max(0f, _excitationDrive[i] - 0.12f);
                    }

                    _nextVm[i] = EvaluateRestingVm(localIsch);
                    float caFloor = isVent ? 0.28f * caOverload : 0f;
                    _calcium[i] = Math.Max(caFloor, _calcium[i] - 0.007f);
                }
                else
                {
                    // Advance phase timer
                    float t = _phaseTimerMs[i] + StepMs;
                    _phaseTimerMs[i] = t;
                    float u = t / Math.Max(1f, apd);

                    if (u >= 1.0f)
                    {
                        _state[i] = StateResting;
                        _phaseTimerMs[i] = 0f;
                        _excitationDrive[i] = 0f;
                        _nextVm[i] = EvaluateRestingVm(localIsch);
                    }
                    else
                    {
                        // Phase 0/1 active wavefront (~22 ms, or ~55 ms in slow AV node) -> Phase 2/3 refractory
                        float upstrokeDuration = (reg == HeartRegion.AVNode) ? 55f : 22f;
                        if (t < upstrokeDuration)
                            _state[i] = StateUpstroke;
                        else
                            _state[i] = StateRefractory;

                        // Relative refractory period (RRP): u > 0.84 allows premature capture if strong active wavefront stimulus
                        float rrpStart = 0.84f + 0.08f * Clamp(amio / 2f, 0f, 1f) + (isVent ? 0.06f * Clamp(lido / 3f, 0f, 1f) * localIsch : 0f);
                        if (u > rrpStart && reg != HeartRegion.AVNode && reg != HeartRegion.SANode)
                        {
                            float iDiff = 0f;
                            int baseOff = i * CardiacAnatomy.MaxNeighbors;
                            for (int k = 0; k < CardiacAnatomy.MaxNeighbors; k++)
                            {
                                int nb = nIdx[baseOff + k];
                                if (nb >= 0 && _state[nb] == StateUpstroke)
                                {
                                    iDiff += nW[baseOff + k] * ((_vm[nb] - vm) / 105f);
                                }
                            }
                            float rrpDepth = (1.0f - u) / Math.Max(0.02f, 1.0f - rrpStart);
                            float rrpThreshold = 1.15f + 2.8f * rrpDepth + 0.35f * amio + (isVent ? 0.5f * lido * localIsch : 0f);
                            float condMult = (1f - 0.5f * localIsch) / tempFactor;
                            float drive = _excitationDrive[i] + Anatomy.BaseAlpha[i] * condMult * iDiff;
                            if (drive >= rrpThreshold)
                            {
                                // Early capture produces shorter APD (restitution) -> key mechanism for VT/VF spiral wave
                                ActivateCell(i, reg, localIsch, amioApdMult, lido, rateApdAdapt, tempFactor, 0.76f);
                                u = _phaseTimerMs[i] / Math.Max(1f, _activeApdMs[i]);
                            }
                            else
                            {
                                _excitationDrive[i] = drive * 0.85f;
                            }
                        }

                        _nextVm[i] = EvaluateActionPotentialVm(reg, u, localIsch);
                    }

                    // Excitation-contraction calcium transient (catecholamines boost peak influx; overload elevates diastolic floor)
                    float caFloorActive = isVent ? 0.28f * caOverload : 0f;
                    if (u < 0.25f)
                        _calcium[i] = Math.Min(1f, _calcium[i] + 0.055f * (1f + 0.35f * Math.Min(adren, 2.0f)));
                    else
                        _calcium[i] = Math.Max(caFloorActive, _calcium[i] - 0.0055f);
                }

                if (isVent && _nextVm[i] > -20f)
                    ventActiveCount++;

                if (reg == HeartRegion.LeftVentricle)
                {
                    float stunFactor = _stunTimerMs[i] > 0f ? 0.45f : 1f;
                    lvTensionSum += _calcium[i] * (1f - 0.60f * localIsch) * stunFactor;
                }
                else if (reg == HeartRegion.RightVentricle)
                {
                    float stunFactor = _stunTimerMs[i] > 0f ? 0.45f : 1f;
                    rvTensionSum += _calcium[i] * (1f - 0.60f * localIsch) * stunFactor;
                }
            }

            // Swap Vm buffer
            for (int i = 0; i < n; i++)
                _vm[i] = _nextVm[i];

            // Fast His-Purkinje propagation micro-steps (3x additional steps along ~120 Purkinje cells -> ~4 cells/ms)
            int[] pkCells = Anatomy.PurkinjeCells;
            for (int sub = 0; sub < 3; sub++)
            {
                for (int p = 0; p < pkCells.Length; p++)
                {
                    int i = pkCells[p];
                    if (_state[i] != StateResting) continue;

                    float iDiff = 0f;
                    int baseOff = i * CardiacAnatomy.MaxNeighbors;
                    for (int k = 0; k < CardiacAnatomy.MaxNeighbors; k++)
                    {
                        int nb = nIdx[baseOff + k];
                        if (nb >= 0 && _state[nb] == StateUpstroke)
                        {
                            HeartRegion nbReg = Anatomy.Regions[nb];
                            if (nbReg == HeartRegion.Purkinje || nbReg == HeartRegion.AVNode)
                            {
                                float dv = _vm[nb] - _vm[i];
                                if (dv > 0f)
                                    iDiff += nW[baseOff + k] * (dv / 105f);
                            }
                        }
                    }

                    if (iDiff > 0.001f)
                    {
                        float drive = _excitationDrive[i] + Anatomy.BaseAlpha[i] * (adrenConductionMult / tempFactor) * iDiff;
                        if (drive >= 1.0f)
                        {
                            ActivateCell(i, HeartRegion.Purkinje, _ischemia[i], amioApdMult, lido, rateApdAdapt, tempFactor, 1.0f);
                            _vm[i] = 18f;
                        }
                        else
                        {
                            _excitationDrive[i] = drive;
                        }
                    }
                }
            }

            VentricularActiveFraction = (float)ventActiveCount / Math.Max(1, Anatomy.VentricularCells.Length);
            _lvInstantTension = lvTensionSum / Math.Max(1, Anatomy.LvCells.Length);
            _rvInstantTension = rvTensionSum / Math.Max(1, Anatomy.RvCells.Length);

            // 6. Real-time beat-by-beat LV ejection + Windkessel arterial hemodynamics -> ECG dipole & metrics
            UpdateArterialHemodynamics(adren, dopa, dopaBeta1);
            UpdateDipoleAndEcg();
            UpdateMechanicalEjection();
        }

        private void ActivateCell(
            int i,
            HeartRegion reg,
            float localIsch,
            float amioApdMult,
            float lido,
            float rateApdAdapt,
            float tempFactor,
            float restitutionMult)
        {
            _state[i] = StateUpstroke;
            _phaseTimerMs[i] = 0f;
            _excitationDrive[i] = 0f;
            _diastoleMs[i] = 0f;

            float baseApd = Anatomy.BaseApdMs[i] * Anatomy.Heterogeneity[i];
            // Rate adaptation applies predominantly to ventricles & Purkinje
            float rateTerm = (reg == HeartRegion.LeftVentricle || reg == HeartRegion.RightVentricle || reg == HeartRegion.Septum || reg == HeartRegion.Purkinje)
                ? rateApdAdapt
                : 1f;

            // Ischemia shortens ventricular APD & increases dispersion
            float ischMult = 1f - 0.30f * localIsch;
            // Lidocaine slightly shortens normal ventricular APD (Class Ib)
            float lidoMult = (reg == HeartRegion.LeftVentricle || reg == HeartRegion.RightVentricle || reg == HeartRegion.Septum)
                ? (1f - 0.04f * Clamp(lido / 3f, 0f, 1.5f))
                : 1f;

            _activeApdMs[i] = Clamp(baseApd * rateTerm * amioApdMult * ischMult * lidoMult * tempFactor * restitutionMult, 52f, 520f);
        }

        private void TriggerSaNode()
        {
            float amio = Clamp(AmiodaroneConcentration, 0f, 4f);
            float amioApdMult = 1f + 0.24f * amio;
            float tempFactor = Clamp(1f + (37f - Environment.TemperatureC) * 0.045f, 0.75f, 1.8f);

            for (int k = 0; k < Anatomy.SaNodeCells.Length; k++)
            {
                int idx = Anatomy.SaNodeCells[k];
                if (_state[idx] == StateResting)
                {
                    ActivateCell(idx, HeartRegion.SANode, _ischemia[idx], amioApdMult, LidocaineConcentration, 1f, tempFactor, 1f);
                    _vm[idx] = 15f;
                }
            }
        }

        private void TriggerAvEscape()
        {
            float amio = Clamp(AmiodaroneConcentration, 0f, 4f);
            float amioApdMult = 1f + 0.24f * amio;
            float tempFactor = Clamp(1f + (37f - Environment.TemperatureC) * 0.045f, 0.75f, 1.8f);

            for (int i = 0; i < Anatomy.CellCount; i++)
            {
                if (Anatomy.Regions[i] == HeartRegion.AVNode && _state[i] == StateResting)
                {
                    ActivateCell(i, HeartRegion.AVNode, _ischemia[i], amioApdMult, LidocaineConcentration, 1f, tempFactor, 1f);
                    _vm[i] = 15f;
                }
            }
        }

        private void TriggerFocalZone(float cx, float cy, float radius)
        {
            float amio = Clamp(AmiodaroneConcentration, 0f, 4f);
            float amioApdMult = 1f + 0.24f * amio;
            float tempFactor = Clamp(1f + (37f - Environment.TemperatureC) * 0.045f, 0.75f, 1.8f);
            float r2 = radius * radius;

            for (int k = 0; k < Anatomy.VentricularCells.Length; k++)
            {
                int idx = Anatomy.VentricularCells[k];
                if (Anatomy.Regions[idx] == HeartRegion.Purkinje) continue;
                float dx = Anatomy.GridX[idx] - cx;
                float dy = Anatomy.GridY[idx] - cy;
                if (dx * dx + dy * dy <= r2 && _state[idx] == StateResting)
                {
                    ActivateCell(idx, Anatomy.Regions[idx], _ischemia[idx], amioApdMult, LidocaineConcentration, 0.9f, tempFactor, 0.72f);
                    _vm[idx] = 15f;
                }
            }
        }

        private static float EvaluateRestingVm(float ischemia) => -85f + 26f * ischemia;

        private static float EvaluateActionPotentialVm(HeartRegion reg, float u, float ischemia)
        {
            float vRest = -85f + 26f * ischemia;
            float vPeak = 20f - 22f * ischemia;
            float vPlat = (reg == HeartRegion.RightAtrium || reg == HeartRegion.LeftAtrium)
                ? (2f - 12f * ischemia)
                : (12f - 36f * ischemia);

            if (u < 0.04f)
            {
                // Phase 0: rapid upstroke
                float t = u / 0.04f;
                return vRest + (vPeak - vRest) * t;
            }

            if (reg == HeartRegion.RightAtrium || reg == HeartRegion.LeftAtrium || reg == HeartRegion.SANode)
            {
                // Atrial AP: brief gentle plateau (u < 0.42) then slow repolarization so Ta wave doesn't distort PR/ST
                if (u < 0.42f)
                {
                    float t = (u - 0.04f) / 0.38f;
                    return vPeak + (vPlat - vPeak) * t;
                }
                float tr = (u - 0.42f) / 0.58f;
                float smooth = tr * tr * (3f - 2f * tr);
                return vPlat + (vRest - vPlat) * smooth;
            }

            if (u < 0.56f)
            {
                // Phase 1-2: gentle plateau slope
                float t = (u - 0.04f) / 0.52f;
                return vPeak + (vPlat - vPeak) * t;
            }

            // Phase 3: smooth S-curve repolarization
            float r = (u - 0.56f) / 0.44f;
            float repol = r * r * (3f - 2f * r);
            return vPlat + (vRest - vPlat) * repol;
        }

        private void UpdatePharmacokinetics()
        {
            // First-order absorption from pending bolus pool into plasma (tau ~ 4 sec for IV push mixing)
            if (AmiodaronePendingPool > 0.0001f)
            {
                float absorbed = AmiodaronePendingPool * (StepSec / 4.0f);
                AmiodaronePendingPool -= absorbed;
                AmiodaroneConcentration += absorbed;
            }
            if (AmiodaroneConcentration > 0f && AmiodaroneHalfLifeSec > 0.1f)
            {
                double k = Math.Log(2.0) / AmiodaroneHalfLifeSec;
                AmiodaroneConcentration *= (float)Math.Exp(-k * StepSec);
            }

            if (LidocainePendingPool > 0.0001f)
            {
                float absorbed = LidocainePendingPool * (StepSec / 2.5f);
                LidocainePendingPool -= absorbed;
                LidocaineConcentration += absorbed;
            }
            if (LidocaineConcentration > 0f && LidocaineHalfLifeSec > 0.1f)
            {
                double k = Math.Log(2.0) / LidocaineHalfLifeSec;
                LidocaineConcentration *= (float)Math.Exp(-k * StepSec);
            }

            // Sodium Nitroprusside: ultra-rapid onset IV vasodilator (tau ~ 1.8s, t1/2 = 50s)
            if (NitroprussidePendingPool > 0.0001f)
            {
                float absorbed = NitroprussidePendingPool * (StepSec / 1.8f);
                NitroprussidePendingPool -= absorbed;
                NitroprussideConcentration += absorbed;
            }
            if (NitroprussideConcentration > 0f && NitroprussideHalfLifeSec > 0.1f)
            {
                double k = Math.Log(2.0) / NitroprussideHalfLifeSec;
                NitroprussideConcentration *= (float)Math.Exp(-k * StepSec);
            }

            // Vasopressin (ADH): pure dose-dependent V1 vasoconstrictor (tau ~ 2.8s, t1/2 = 120s)
            if (VasopressinPendingPool > 0.0001f)
            {
                float absorbed = VasopressinPendingPool * (StepSec / 2.8f);
                VasopressinPendingPool -= absorbed;
                VasopressinConcentration += absorbed;
            }
            if (VasopressinConcentration > 0f && VasopressinHalfLifeSec > 0.1f)
            {
                double k = Math.Log(2.0) / VasopressinHalfLifeSec;
                VasopressinConcentration *= (float)Math.Exp(-k * StepSec);
            }

            // Dopamine: dose-staged catecholamine (tau ~ 2.4s, t1/2 = 75s)
            if (DopaminePendingPool > 0.0001f)
            {
                float absorbed = DopaminePendingPool * (StepSec / 2.4f);
                DopaminePendingPool -= absorbed;
                DopamineConcentration += absorbed;
            }
            if (DopamineConcentration > 0f && DopamineHalfLifeSec > 0.1f)
            {
                double k = Math.Log(2.0) / DopamineHalfLifeSec;
                DopamineConcentration *= (float)Math.Exp(-k * StepSec);
            }
        }

        private void UpdateArterialHemodynamics(float adren, float dopa, float dopaBeta1)
        {
            // Smooth instantaneous LV calcium tension (tau ~ 12 ms)
            _prevLvTensionSmooth = _lvTensionSmooth;
            _lvTensionSmooth += (_lvInstantTension - _lvTensionSmooth) * 0.082f;

            // Slow diastolic baseline tracker: drops swiftly when tension relaxes, drifts up slowly during sustained VF tonicity
            if (_lvTensionSmooth < _lvTensionFloor)
                _lvTensionFloor += (_lvTensionSmooth - _lvTensionFloor) * 0.055f;
            else
                _lvTensionFloor += (_lvTensionSmooth - _lvTensionFloor) * (StepSec / 1.35f);

            float floorWeight = CurrentRhythm == RhythmType.VentricularFibrillation ? 1.0f : 0.30f;
            float activeTension = Math.Max(0f, _lvTensionSmooth - floorWeight * _lvTensionFloor);
            float dTensionDt = (_lvTensionSmooth - _prevLvTensionSmooth) / StepSec;

            // 1. Effective vascular tone & vessel diameter (combining environment + direct peripheral vasoactive drugs)
            float baseTone = Clamp(Environment.VascularTone > 0.01f ? Environment.VascularTone : 1.0f, 0.20f, 3.60f);
            float baseDiam = Clamp(Environment.VesselDiameter > 0.01f ? Environment.VesselDiameter : 1.0f, 0.50f, 1.80f);
            float viscosity = Clamp(Environment.BloodViscosityFactor > 0.01f ? Environment.BloodViscosityFactor : 1.0f, 0.45f, 2.20f);
            float preload = Clamp(Environment.PreloadFactor, 0.02f, 1.35f);
            float venousReturn = Clamp(Environment.VenousReturnFactor > 0.01f ? Environment.VenousReturnFactor : 1.0f, 0.25f, 1.45f);

            // When an external autonomic mod (e.g. NeurologyOverhaulMod) drives body.bloodVesselSize,
            // avoid double-applying endogenous sympathetic vasoconstriction; only add exogenous adrenaline excess.
            float adrenAlpha = Environment.ExternalAutonomicControl
                ? Math.Max(0f, adren - 0.65f)
                : adren;
            float adrenToneBoost = 0.48f * (float)Math.Tanh(adrenAlpha * 0.65) + 0.45f * Math.Max(0f, adrenAlpha - 1.0f);

            // Clinical dose-staged Dopamine:
            // - Low/moderate (< 1.0): predominantly beta1 inotropy & chronotropy with mild tone support
            // - High (> 1.0): strong alpha1 vasoconstriction
            float dopaAlpha = Math.Max(0f, dopa - 1.0f);
            float dopaToneBoost = 0.08f * Clamp(dopa, 0f, 1.0f) + 0.65f * (float)Math.Tanh(dopaAlpha * 0.55);

            // Vasopressin (ADH): pure dose-dependent V1 vasoconstriction (no direct chronotropic effect)
            float vaso = Math.Max(0f, VasopressinConcentration);
            float vasoToneBoost = 0.95f * (float)Math.Tanh(vaso * 0.48) + 0.12f * Math.Max(0f, vaso - 2.0f);

            // Sodium Nitroprusside: potent dose-dependent arterial + venous vasodilation
            float nitro = Math.Max(0f, NitroprussideConcentration);
            float nitroDilator = 1f / (1f + 0.80f * nitro);
            float nitroVenous = 1f / (1f + 0.22f * nitro);

            float constrictMult = 1f + adrenToneBoost + dopaToneBoost + vasoToneBoost;
            float effTone = Clamp(baseTone * constrictMult * nitroDilator, 0.18f, 3.80f);
            float drugDiamFactor = Clamp((float)Math.Pow(1.0 / Math.Max(0.22, constrictMult * nitroDilator), 0.18), 0.66f, 1.48f);
            float effDiam = Clamp(baseDiam * drugDiamFactor, 0.55f, 1.68f);

            _effectiveVascularTone = effTone;
            _effectiveVesselDiameter = effDiam;

            // 2. Diastolic ventricular filling (shorter diastole during PVC/VT -> naturally smaller stroke volume!)
            float fillTarget = Clamp(preload * venousReturn * nitroVenous, 0.04f, 1.40f);
            if (!_lvValveOpen)
            {
                _lvDiastolicFillFrac += (fillTarget - _lvDiastolicFillFrac) * (StepSec / 0.185f);
            }

            // 3. Beat-by-beat LV pressure & aortic valve ejection Q_LV(t)
            float atpContractility = Clamp(0.22f + 0.78f * MyocardialAtpReserve, 0.05f, 1.0f);
            float inotropy = (1f + 0.78f * (float)Math.Tanh(adren * 0.75) + 0.34f * dopaBeta1) * atpContractility;
            float syncFactor = CurrentRhythm == RhythmType.Asystole
                ? 0f
                : (CurrentRhythm == RhythmType.VentricularFibrillation
                    ? 0.05f
                    : Clamp(0.22f + 0.78f * VentricularSynchrony, 0.15f, 1.0f));

            // Frank-Starling contractile force is set by End-Diastolic Filling at beat onset
            float effectiveFillForForce = _lvValveOpen ? _beatStartFillFrac : _lvDiastolicFillFrac;
            float pLv = 6f + 265f * activeTension * (0.38f + 0.62f * effectiveFillForForce) * inotropy * syncFactor;

            float qLv = 0f;
            if (pLv > _arterialPressureMmHg && _lvDiastolicFillFrac > 0.16f && activeTension > 0.05f)
            {
                if (!_lvValveOpen)
                {
                    _lvValveOpen = true;
                    _beatStartFillFrac = _lvDiastolicFillFrac;
                }
                qLv = Clamp((pLv - _arterialPressureMmHg) / 0.115f, 0f, 880f);
                float ejectedMl = qLv * StepSec;
                _currentBeatEjectedMl += ejectedMl;
                // Deplete ventricular volume toward residual ESV (~16% floor)
                _lvDiastolicFillFrac = Math.Max(0.12f, _lvDiastolicFillFrac - ejectedMl / 96f);
            }
            else if (_lvValveOpen && (pLv <= _arterialPressureMmHg || _lvDiastolicFillFrac <= 0.16f || dTensionDt < -2.0f))
            {
                if (_currentBeatEjectedMl >= 4f)
                {
                    LastBeatLvStrokeVolumeMl = _currentBeatEjectedMl;
                    CompletedBeatCount++;
                    _dicroticTimerSec = 0.055f;
                }
                _lvValveOpen = false;
                _currentBeatEjectedMl = 0f;
            }
            InstantLvFlowMlPerSec = qLv;

            // 4. Dicrotic notch aortic valve closure transient (nearly zero net volume)
            float qDicrotic = 0f;
            if (_dicroticTimerSec > 0f)
            {
                _dicroticTimerSec = Math.Max(0f, _dicroticTimerSec - StepSec);
                qDicrotic = _dicroticTimerSec > 0.035f ? -26f : 18f;
            }

            // 5. Mechanical CPR chest-compression pulse (AutoPump)
            float qCpr = 0f;
            if (_cprPulseDurationSec > 0f && _cprPulseElapsedSec < _cprPulseDurationSec)
            {
                float phase = _cprPulseElapsedSec / _cprPulseDurationSec;
                float effectiveCprVol = _cprPulseTotalMl * Clamp(preload, 0.12f, 1.15f);
                // Half-sine integral over [0, D] equals effectiveCprVol
                qCpr = (float)(Math.PI / (2.0 * _cprPulseDurationSec) * effectiveCprVol * Math.Sin(Math.PI * phase));
                _cprPulseElapsedSec += StepSec;
            }

            // 6. 2-element Windkessel ODE integration
            // Baseline calibrated so 72 bpm, SV ~ 75 mL, tone = 1, diam = 1 yields ~120/80 mmHg (MAP ~ 93 mmHg)
            float baseDiamRes = 1f / (baseDiam * baseDiam);
            float svr = Clamp(0.91f * effTone * baseDiamRes * viscosity, 0.20f, 4.80f);
            float wallStiffFactor = Clamp(1f - Math.Max(0f, _arterialPressureMmHg - 115f) / 360f, 0.50f, 1.0f);
            float compliance = Clamp((1.62f / (0.65f + 0.35f * effTone)) * wallStiffFactor, 0.42f, 2.45f);
            float pVenous = Clamp(8.0f * fillTarget, 2.0f, 14.0f);

            float qOut = (_arterialPressureMmHg - pVenous) / svr;
            float dPdt = (qLv + qCpr + qDicrotic - qOut) / compliance;
            _arterialPressureMmHg = Clamp(_arterialPressureMmHg + dPdt * StepSec, 0f, 290f);

            // 7. Rolling 5-second max (SBP), min (DBP), and mean (MAP) updated every 50 ms
            if ((_stepCounter % 50) == 0 && _abpValidSamples > 0)
            {
                int windowSamples = Math.Min(2500, Math.Min(_abpValidSamples, RingCapacity));
                float maxP = float.MinValue;
                float minP = float.MaxValue;
                double sumP = 0.0;
                for (int k = 0; k < windowSamples; k++)
                {
                    int idx = (_ringWritePos - 1 - k + RingCapacity) % RingCapacity;
                    float p = _abpRingBuffer[idx];
                    if (p > maxP) maxP = p;
                    if (p < minP) minP = p;
                    sumP += p;
                }
                _systolicBp5s = maxP;
                _diastolicBp5s = minP;
                _map5s = (float)(sumP / windowSamples);
            }
        }

        private void UpdateDipoleAndEcg()
        {
            // Dipole vector p = - grad(Vm) * MassWeight
            float dxSum = 0f;
            float dySum = 0f;
            int n = Anatomy.CellCount;
            int[] gL = Anatomy.GradLeft;
            int[] gR = Anatomy.GradRight;
            int[] gU = Anatomy.GradUp;
            int[] gD = Anatomy.GradDown;
            float[] mw = Anatomy.MassWeight;

            for (int i = 0; i < n; i++)
            {
                int l = gL[i];
                int r = gR[i];
                int u = gU[i];
                int d = gD[i];

                float vCenter = _vm[i];
                float vL = l >= 0 ? _vm[l] : vCenter;
                float vR = r >= 0 ? _vm[r] : vCenter;
                float vU = u >= 0 ? _vm[u] : vCenter;
                float vD = d >= 0 ? _vm[d] : vCenter;

                float px = -(vR - vL) * 0.5f;
                float py = -(vD - vU) * 0.5f;

                float w = mw[i];
                dxSum += px * w;
                dySum += py * w;
            }

            // Frontal leads I & II
            float rawI = dxSum * _normScale;
            float rawII = (0.5f * dxSum + 0.8660254f * dySum) * _normScale;

            // Monitor AC-coupled high-pass filter (tau = 1.6s -> ~0.1 Hz cutoff, preserves ST elevation/depression)
            const float hpAlpha = 0.999375f; // exp(-0.001 / 1.6)
            _hpStateI = hpAlpha * (_hpStateI + rawI - _prevRawI);
            _hpStateII = hpAlpha * (_hpStateII + rawII - _prevRawII);
            _prevRawI = rawI;
            _prevRawII = rawII;

            // Low-pass smoothing (tau = 6 ms -> ~26 Hz)
            const float lpAlpha = 0.1535f; // 1 - exp(-0.001 / 0.006)
            _lpStateI += lpAlpha * (_hpStateI - _lpStateI);
            _lpStateII += lpAlpha * (_hpStateII - _lpStateII);

            float outI = _lpStateI;
            float outII = _lpStateII;

            // Coarse VF <-> Fine VF amplitude modulation driven by myocardial ATP reserve
            if (CurrentRhythm == RhythmType.VentricularFibrillation)
            {
                float vfAmpScale = 0.18f + 0.82f * Clamp((MyocardialAtpReserve - 0.02f) / 0.53f, 0f, 1f);
                outI *= vfAmpScale;
                outII *= vfAmpScale;
            }

            // Defibrillator monitor shock artifact
            if (_shockArtifactTimerSec > 0f)
            {
                _shockArtifactTimerSec -= StepSec;
                float spike = _shockArtifactTimerSec > 0.10f ? 1.4f * _shockArtifactPolarity : -0.45f * (_shockArtifactTimerSec / 0.10f);
                outI = Clamp(outI + spike * 0.6f, -1.6f, 1.6f);
                outII = Clamp(outII + spike, -1.6f, 1.6f);
            }

            // R-peak detection
            if (_rPeakLockoutMs > 0f) _rPeakLockoutMs -= StepMs;

            bool isLocalPeak = _prevEcgII < _lastEcgII && outII < _lastEcgII;
            if (isLocalPeak && _lastEcgII > 0.45f && _rPeakLockoutMs <= 0f)
            {
                _rPeakLockoutMs = 190f; // Avoid double-counting T waves
                float rr = (SimulationTimeSec - _lastRPeakTimeSec) * 1000f;
                if (rr >= 200f && rr <= 3000f)
                {
                    _measuredRrMs = _measuredRrMs * 0.45f + rr * 0.55f;
                    MeasuredHeartRateBpm = 60000f / _measuredRrMs;
                }
                _lastRPeakTimeSec = SimulationTimeSec;
                _recentVentricularBeats++;
            }

            // Decay HR toward 0 if asystole (no R peak for > 2.5 s)
            if (SimulationTimeSec - _lastRPeakTimeSec > 2.5f && VentricularActiveFraction < 0.05f)
            {
                MeasuredHeartRateBpm = Math.Max(0f, MeasuredHeartRateBpm - 0.04f);
            }

            _prevEcgII = _lastEcgII;
            _lastEcgII = outII;

            var sample = new FrontalEcgSample(SimulationTimeSec, outI, outII);
            LatestSample = sample;

            // Downsample 1000 Hz -> 500 Hz into ring buffer (synchronized ECG + arterial pressure)
            if ((_stepCounter & 1) == 0)
            {
                _ringBuffer[_ringWritePos] = sample;
                _abpRingBuffer[_ringWritePos] = _arterialPressureMmHg;
                _ringWritePos = (_ringWritePos + 1) % RingCapacity;
                if (_abpValidSamples < RingCapacity) _abpValidSamples++;
            }
        }

        private void UpdateMechanicalEjection()
        {
            if (_lvInstantTension > _lvTensionSysPeak) _lvTensionSysPeak = _lvInstantTension;
            if (_lvInstantTension < _lvTensionDiaTrough) _lvTensionDiaTrough = _lvInstantTension;
            if (_rvInstantTension > _rvTensionSysPeak) _rvTensionSysPeak = _rvInstantTension;
            if (_rvInstantTension < _rvTensionDiaTrough) _rvTensionDiaTrough = _rvInstantTension;

            if (VentricularActiveFraction > _ventActivePeakInWindow) _ventActivePeakInWindow = VentricularActiveFraction;
            if (VentricularActiveFraction < _ventActiveTroughInWindow) _ventActiveTroughInWindow = VentricularActiveFraction;

            _beatWindowTimerMs += StepMs;
            _beatCountWindowTimerSec += StepSec;

            // Update stroke volume every ~1450 ms (covers bradycardia down to 42 bpm) OR every 400 ms during VT/VF
            bool forceVfCheck = _beatWindowTimerMs >= 400f &&
                                (CurrentRhythm == RhythmType.VentricularFibrillation ||
                                 CurrentRhythm == RhythmType.VentricularTachycardia ||
                                 ((SimulationTimeSec - _lastRPeakTimeSec > 0.95f) && VentricularActiveFraction > 0.14f));
            bool regularBeatBoundary = _beatWindowTimerMs >= 1450f || (MeasuredHeartRateBpm > 68f && _beatWindowTimerMs >= 950f);
            if (regularBeatBoundary || forceVfCheck)
            {
                float winPeak = _ventActivePeakInWindow;
                float winTrough = _ventActiveTroughInWindow;

                // In normal sinus or organized VT/PVCs, peak reaches coherent activation (>= 0.45);
                // a single overlapping PVC beat should not zero out synchrony for healthy sinus beats.
                // In fragmented VF, cells fire asynchronously so trough remains > 0.06 and peak stays suppressed (< 0.45).
                float diastolicRest = (winPeak >= 0.45f && CurrentRhythm != RhythmType.VentricularFibrillation)
                    ? Clamp(1f - Math.Max(0f, winTrough - 0.12f) / 0.25f, 0.55f, 1f)
                    : Clamp(1f - Math.Max(0f, winTrough - 0.03f) / 0.14f, 0f, 1f);
                float systolicPeak = Clamp((winPeak - 0.12f) / 0.50f, 0f, 1f);
                float synchrony = Clamp(diastolicRest * systolicPeak, 0f, 1f);
                VentricularSynchrony = VentricularSynchrony * 0.35f + synchrony * 0.65f;

                bool looksDisorganizedVf = VentricularSynchrony < 0.26f && winTrough > 0.06f && winPeak < 0.45f;
                if (looksDisorganizedVf)
                    _vfConfirmWindows++;
                else
                    _vfConfirmWindows = 0;

                // Update rhythm classification BEFORE computing SV so VF/VT clamping applies immediately
                if (CurrentRhythm == RhythmType.Asystole ||
                    MyocardialAtpReserve <= 0.05f ||
                    (Environment.TargetSinusBpm <= 1f && MeasuredHeartRateBpm < 10f && VentricularActiveFraction < 0.02f && CurrentRhythm != RhythmType.VentricularFibrillation))
                {
                    CurrentRhythm = RhythmType.Asystole;
                    VentricularSynchrony = 0f;
                }
                else if (CurrentRhythm == RhythmType.VentricularFibrillation || (CurrentRhythm != RhythmType.VentricularTachycardia && _vfConfirmWindows >= 2))
                {
                    CurrentRhythm = RhythmType.VentricularFibrillation;
                    VentricularSynchrony = Math.Min(VentricularSynchrony, 0.16f);
                }
                else if (CurrentRhythm == RhythmType.VentricularTachycardia || (MeasuredHeartRateBpm > 145f && VentricularSynchrony < 0.72f))
                {
                    CurrentRhythm = RhythmType.VentricularTachycardia;
                    VentricularSynchrony = Clamp(VentricularSynchrony, 0.35f, 0.55f);
                }
                else if (MeasuredHeartRateBpm < 60f)
                    CurrentRhythm = RhythmType.SinusBradycardia;
                else if (MeasuredHeartRateBpm > 100f)
                    CurrentRhythm = RhythmType.SinusTachycardia;
                else
                    CurrentRhythm = RhythmType.NormalSinus;

                float lvPulse = Clamp((_lvTensionSysPeak - _lvTensionDiaTrough) / 0.78f, 0f, 1.15f);
                float rvPulse = Clamp((_rvTensionSysPeak - _rvTensionDiaTrough) / 0.78f, 0f, 1.15f);

                // Frank-Starling preload + high-rate diastolic filling impairment (above 130 bpm filling drops)
                float preload = Clamp(Environment.PreloadFactor, 0f, 1.35f);
                float hr = MeasuredHeartRateBpm;
                float fillingEff = hr <= 130f ? 1f : Clamp(1f - (hr - 130f) / 180f, 0.25f, 1f);
                float adrenTotal = Math.Max(0f, Environment.Adrenaline);
                float dopaBeta1 = Clamp(DopamineConcentration * 0.50f, 0f, 1.15f);
                float adrenInotropy = 1f + 0.28f * (float)Math.Tanh(adrenTotal * 1.1) + 0.26f * dopaBeta1;

                float targetLvSv = 70f * lvPulse * VentricularSynchrony * preload * fillingEff * adrenInotropy;
                float targetRvSv = 70f * rvPulse * VentricularSynchrony * preload * fillingEff * adrenInotropy;

                if (CurrentRhythm == RhythmType.VentricularTachycardia)
                {
                    targetLvSv *= 0.55f;
                    targetRvSv *= 0.55f;
                }

                // Zero out tiny residual ripple in pure VF/asystole
                if (VentricularSynchrony < 0.18f)
                {
                    targetLvSv *= VentricularSynchrony / 0.18f * 0.25f;
                    targetRvSv *= VentricularSynchrony / 0.18f * 0.25f;
                }

                _lvStrokeVolumeMl = _lvStrokeVolumeMl * 0.4f + targetLvSv * 0.6f;
                _rvStrokeVolumeMl = _rvStrokeVolumeMl * 0.4f + targetRvSv * 0.6f;
                _lvEjectionFraction = Clamp(0.60f * (_lvStrokeVolumeMl / 70f), 0f, 0.78f);
                _rvEjectionFraction = Clamp(0.60f * (_rvStrokeVolumeMl / 70f), 0f, 0.78f);

                // Reset window trackers with slight decay so trough/peak remain responsive
                _lvTensionSysPeak = _lvInstantTension;
                _lvTensionDiaTrough = _lvInstantTension;
                _rvTensionSysPeak = _rvInstantTension;
                _rvTensionDiaTrough = _rvInstantTension;
                _ventActivePeakInWindow = VentricularActiveFraction;
                _ventActiveTroughInWindow = VentricularActiveFraction;
                _beatWindowTimerMs = 0f;
            }

            if (_beatCountWindowTimerSec >= 1.5f)
            {
                _recentVentricularBeats = 0;
                _beatCountWindowTimerSec = 0f;
            }
        }

        /// <summary>
        /// Sample the ring buffer at (SimulationTimeSec - offsetSec) for smooth ECGVisualizer playback.
        /// </summary>
        public FrontalEcgSample SampleAtOffset(float offsetSec)
        {
            int backSamples = ClampInt((int)Math.Round(offsetSec * SampleRateHz), 0, RingCapacity - 1);
            int idx = (_ringWritePos - 1 - backSamples + RingCapacity) % RingCapacity;
            return _ringBuffer[idx];
        }

        internal void CopyRingBufferTo(FrontalEcgSample[] destination, out int writePos)
        {
            Array.Copy(_ringBuffer, destination, RingCapacity);
            writePos = _ringWritePos;
        }

        internal void CopyAbpRingBufferTo(float[] destination, out int writePos)
        {
            Array.Copy(_abpRingBuffer, destination, RingCapacity);
            writePos = _ringWritePos;
        }

        public float SampleArterialPressureAtOffset(float offsetSec)
        {
            int backSamples = ClampInt((int)Math.Round(offsetSec * SampleRateHz), 0, RingCapacity - 1);
            int idx = (_ringWritePos - 1 - backSamples + RingCapacity) % RingCapacity;
            return _abpRingBuffer[idx];
        }

        public float SampleArterialPressurePeakPreserving(float offsetSec, float windowSec)
        {
            int centerBack = ClampInt((int)Math.Round(offsetSec * SampleRateHz), 0, RingCapacity - 1);
            int span = Math.Max(1, (int)Math.Round(windowSec * SampleRateHz));
            float sum = 0f;
            float maxVal = float.MinValue;
            float minVal = float.MaxValue;
            for (int k = 0; k < span; k++)
            {
                int b = Math.Min(RingCapacity - 1, centerBack + k);
                int idx = (_ringWritePos - 1 - b + RingCapacity) % RingCapacity;
                float p = _abpRingBuffer[idx];
                sum += p;
                if (p > maxVal) maxVal = p;
                if (p < minVal) minVal = p;
            }
            float avg = sum / span;
            // Preserve crisp systolic peaks or steep dicrotic dips within narrow pixel windows
            return (maxVal - avg) >= (avg - minVal) ? maxVal : minVal;
        }

        /// <summary>
        /// Peak-preserving readout over a window (e.g. 28 ms ECGVisualizer tick) so narrow R-waves never get clipped.
        /// </summary>
        public float SampleLeadPeakPreserving(FrontalLead lead, float offsetSec, float windowSec)
        {
            int centerBack = ClampInt((int)Math.Round(offsetSec * SampleRateHz), 0, RingCapacity - 1);
            int span = Math.Max(1, (int)Math.Round(windowSec * SampleRateHz));
            float best = 0f;
            float bestAbs = -1f;
            for (int k = 0; k < span; k++)
            {
                int b = Math.Min(RingCapacity - 1, centerBack + k);
                int idx = (_ringWritePos - 1 - b + RingCapacity) % RingCapacity;
                float v = _ringBuffer[idx].GetLead(lead);
                float av = Math.Abs(v);
                if (av > bestAbs)
                {
                    bestAbs = av;
                    best = v;
                }
            }
            return best;
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        private static int ClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
