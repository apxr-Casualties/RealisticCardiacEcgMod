using System;

namespace CardiacSimCore
{
    /// <summary>
    /// Anatomical tissue region of a cell on the 2D frontal cardiac cross-section.
    /// </summary>
    public enum HeartRegion : byte
    {
        None = 0,
        SANode = 1,
        RightAtrium = 2,
        LeftAtrium = 3,
        AVNode = 4,
        Purkinje = 5,
        Septum = 6,
        RightVentricle = 7,
        LeftVentricle = 8
    }

    /// <summary>
    /// Frontal plane ECG lead selector (Einthoven & Goldberger augmented limb leads).
    /// </summary>
    public enum FrontalLead : byte
    {
        I = 0,
        II = 1,
        III = 2,
        aVR = 3,
        aVL = 4,
        aVF = 5
    }

    /// <summary>
    /// High-level rhythm classification inferred from grid electrophysiology & R-peak intervals.
    /// </summary>
    public enum RhythmType : byte
    {
        NormalSinus = 0,
        SinusBradycardia = 1,
        SinusTachycardia = 2,
        VentricularTachycardia = 3,
        VentricularFibrillation = 4,
        Asystole = 5
    }

    /// <summary>
    /// Snapshot of frontal-plane ECG leads at one sample instant.
    /// </summary>
    public readonly struct FrontalEcgSample
    {
        public readonly float TimeSec;
        public readonly float I;
        public readonly float II;
        public readonly float III;
        public readonly float aVR;
        public readonly float aVL;
        public readonly float aVF;

        public FrontalEcgSample(float timeSec, float leadI, float leadII)
        {
            TimeSec = timeSec;
            I = leadI;
            II = leadII;
            III = leadII - leadI;
            aVR = -0.5f * (leadI + leadII);
            aVL = leadI - 0.5f * leadII;
            aVF = leadII - 0.5f * leadI;
        }

        public float GetLead(FrontalLead lead) => lead switch
        {
            FrontalLead.I => I,
            FrontalLead.II => II,
            FrontalLead.III => III,
            FrontalLead.aVR => aVR,
            FrontalLead.aVL => aVL,
            FrontalLead.aVF => aVF,
            _ => II
        };
    }

    /// <summary>
    /// Physiological & environmental inputs fed from game state into the cardiac simulator.
    /// </summary>
    public struct CardiacEnvironmentInput
    {
        /// <summary>Target intrinsic autonomic sinus rate in BPM (e.g. 70 normal, 140 stress/pain, 45 hypothermia/vagal).</summary>
        public float TargetSinusBpm;

        /// <summary>Global myocardial hypoxia/ischemia severity [0..1], e.g. from low SpO2 or shock.</summary>
        public float GlobalIschemia;

        /// <summary>Focal LV lateral/anterior coronary ischemia severity [0..1] (subepicardial/transmural STEMI zone).</summary>
        public float FocalTransmuralIschemia;

        /// <summary>Subendocardial LV ischemia severity [0..1] (NSTEMI / demand ischemia -> ST depression).</summary>
        public float SubendocardialIschemia;

        /// <summary>Focal RV ischemia severity [0..1] (e.g. pulmonary embolism / RCA occlusion).</summary>
        public float RightVentricularIschemia;

        /// <summary>Effective blood volume / preload factor [0..1.5], 1.0 = normal euvolemia.</summary>
        public float PreloadFactor;

        /// <summary>Body temperature in Celsius (37.0 normal). Hypothermia slows conduction & prolongs APD.</summary>
        public float TemperatureC;

        /// <summary>Sympathetic tone / adrenaline [0..1]. Increases sinus rate, conduction, and contractility.</summary>
        public float Adrenaline;

        /// <summary>True when arterial perfusion and oxygenation (BP > 60 and SpO2 > 50, e.g. AutoPump CPR) support myocardial ATP recovery.</summary>
        public bool AllowAtpRecovery;

        /// <summary>Baseline systemic vascular wall tone [0.35..2.40], 1.0 = normal tone (higher = stiffer/constricted, lower = vasoplegic).</summary>
        public float VascularTone;

        /// <summary>Baseline systemic resistance vessel diameter [0.55..1.65], 1.0 = normal (&lt;1 = constricted, &gt;1 = dilated; maps Body.bloodVesselSize).</summary>
        public float VesselDiameter;

        /// <summary>Blood viscosity multiplier [0.65..1.65], 1.0 = normal viscosity.</summary>
        public float BloodViscosityFactor;

        /// <summary>Venous return / systemic filling pressure multiplier [0.15..1.35], 1.0 = normal.</summary>
        public float VenousReturnFactor;

        /// <summary>True when an external autonomic nervous system mod (e.g. NeurologyOverhaulMod) already drives VesselDiameter via Body.bloodVesselSize.</summary>
        public bool ExternalAutonomicControl;

        public static CardiacEnvironmentInput Normal() => new CardiacEnvironmentInput
        {
            TargetSinusBpm = 72f,
            GlobalIschemia = 0f,
            FocalTransmuralIschemia = 0f,
            SubendocardialIschemia = 0f,
            RightVentricularIschemia = 0f,
            PreloadFactor = 1.0f,
            TemperatureC = 37.0f,
            Adrenaline = 0.0f,
            AllowAtpRecovery = false,
            VascularTone = 1.0f,
            VesselDiameter = 1.0f,
            BloodViscosityFactor = 1.0f,
            VenousReturnFactor = 1.0f,
            ExternalAutonomicControl = false
        };
    }
}
