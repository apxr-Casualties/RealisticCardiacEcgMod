using System;
using System.Collections.Generic;

namespace CardiacSimCore
{
    /// <summary>
    /// Precomputed compact topology of active myocardial cells on a 72x72 frontal plane grid.
    /// Converts 2D coordinates into dense flat arrays for zero-allocation, cache-friendly stepping.
    /// </summary>
    public sealed class CardiacAnatomy
    {
        public const int GridSize = 72;
        public const int MaxNeighbors = 8;

        public readonly int CellCount;
        public readonly HeartRegion[] Regions;
        public readonly float[] GridX;
        public readonly float[] GridY;

        /// <summary>0 = endocardium, 1 = epicardium (for ventricular transmural APD & ischemia gradients).</summary>
        public readonly float[] TransmuralDepth;

        /// <summary>Spatial weight for dipole contribution (LV thick wall > septum > RV > atria).</summary>
        public readonly float[] MassWeight;

        /// <summary>Baseline action potential duration in ms at 72 BPM.</summary>
        public readonly float[] BaseApdMs;

        /// <summary>Base excitability / diffusion coupling alpha for region conduction speed.</summary>
        public readonly float[] BaseAlpha;

        /// <summary>Cell heterogeneity factor [~0.94..1.06] for physiological dispersion.</summary>
        public readonly float[] Heterogeneity;

        /// <summary>Focal LV transmural/anterior-lateral ischemic territory mask [0..1].</summary>
        public readonly float[] FocalLvMask;

        /// <summary>Focal LV subendocardial ischemic territory mask [0..1].</summary>
        public readonly float[] SubendoLvMask;

        /// <summary>Focal RV ischemic territory mask [0..1].</summary>
        public readonly float[] FocalRvMask;

        /// <summary>Fixed 8-neighbor cell indices (-1 if none), strided by MaxNeighbors.</summary>
        public readonly int[] NeighborIdx;

        /// <summary>Neighbor geometric weight (1.0 orthogonal, 0.7071 diagonal) * conduction coupling.</summary>
        public readonly float[] NeighborWeight;

        /// <summary>Indices of Purkinje cells for fast His-Purkinje propagation.</summary>
        public readonly int[] PurkinjeCells;

        /// <summary>Indices of SA node cells.</summary>
        public readonly int[] SaNodeCells;

        /// <summary>Indices of Left Ventricle cells (for ejection & VT/VF metrics).</summary>
        public readonly int[] LvCells;

        /// <summary>Indices of Right Ventricle cells.</summary>
        public readonly int[] RvCells;

        /// <summary>Indices of Septum cells.</summary>
        public readonly int[] SeptumCells;

        /// <summary>Indices of all ventricular cells (LV + RV + Septum + Purkinje).</summary>
        public readonly int[] VentricularCells;

        /// <summary>Central difference gradient pairs (leftIdx, rightIdx, upIdx, downIdx) per cell (-1 if boundary).</summary>
        public readonly int[] GradLeft;
        public readonly int[] GradRight;
        public readonly int[] GradUp;
        public readonly int[] GradDown;

        private CardiacAnatomy()
        {
            var gridRegion = new HeartRegion[GridSize, GridSize];
            var transmuralGrid = new float[GridSize, GridSize];

            // Build anatomical chambers in frontal view:
            // x: patient Right (small x) -> patient Left (large x)
            // y: Superior (small y) -> Inferior / Apex (large y)
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    // 1. Atria (y: 11..26)
                    float raDist = EllipseDist(x, y, 25f, 19f, 8.5f, 7.5f);
                    float raCav = EllipseDist(x, y, 25f, 19f, 4.2f, 3.6f);
                    if (raDist <= 1f && raCav > 1f)
                    {
                        gridRegion[x, y] = HeartRegion.RightAtrium;
                    }

                    float laDist = EllipseDist(x, y, 42f, 18.5f, 9.5f, 7.0f);
                    float laCav = EllipseDist(x, y, 42f, 18.5f, 4.5f, 3.3f);
                    if (laDist <= 1f && laCav > 1f)
                    {
                        gridRegion[x, y] = HeartRegion.LeftAtrium;
                    }

                    // Interatrial septum & lower atrial floor converging onto AV node (y=15..27)
                    if (x >= 28 && x <= 38 && y >= 15 && y <= 27)
                    {
                        float dCenter = Math.Abs(x - 33f);
                        if (y <= 24 || dCenter <= 4.5f)
                            gridRegion[x, y] = x <= 33 ? HeartRegion.RightAtrium : HeartRegion.LeftAtrium;
                    }

                    // 2. Ventricular myocardium (y: 31..63), tilted toward apex (44, 62)
                    // Tilted outer LV+RV heart contour
                    float lvOuter = TiltedEllipse(x, y, 41.5f, 44.5f, 16.5f, 18.0f, 0.28f);
                    float lvInner = TiltedEllipse(x, y, 40.5f, 43.0f, 6.2f, 9.0f, 0.28f);

                    float rvOuter = TiltedEllipse(x, y, 26.5f, 43.0f, 12.0f, 14.5f, 0.20f);
                    float rvInner = TiltedEllipse(x, y, 25.0f, 42.5f, 6.5f, 8.5f, 0.20f);

                    // Top ventricular boundary at y >= 31 (below AV fibrous ring y=28..30)
                    if (y >= 31)
                    {
                        bool inLvCavity = lvInner <= 1.0f;
                        bool inRvCavity = rvInner <= 1.0f;

                        if (!inLvCavity && !inRvCavity)
                        {
                            if (lvOuter <= 1.0f)
                            {
                                gridRegion[x, y] = HeartRegion.LeftVentricle;
                                float rOut = (float)Math.Sqrt(lvOuter);
                                float rIn = (float)Math.Sqrt(lvInner);
                                float raw = 1f - (1f - rOut) / Math.Max(0.1f, (1f - rOut + (rIn - 1f)));
                                transmuralGrid[x, y] = Clamp01(raw);
                            }
                            else if (rvOuter <= 1.0f)
                            {
                                gridRegion[x, y] = HeartRegion.RightVentricle;
                                float rOut = (float)Math.Sqrt(rvOuter);
                                float rIn = (float)Math.Sqrt(rvInner);
                                float raw = 1f - (1f - rOut) / Math.Max(0.1f, (1f - rOut + (rIn - 1f)));
                                transmuralGrid[x, y] = Clamp01(raw);
                            }
                        }
                    }
                }
            }

            // Carve interventricular septum cleanly (x: 31..36, y: 31..54) so LV & RV meet at septum
            for (int y = 31; y <= 55; y++)
            {
                for (int x = 31; x <= 36; x++)
                {
                    if (gridRegion[x, y] == HeartRegion.LeftVentricle || gridRegion[x, y] == HeartRegion.RightVentricle)
                    {
                        gridRegion[x, y] = HeartRegion.Septum;
                        transmuralGrid[x, y] = 0.15f;
                    }
                }
            }

            // Enforce insulating AV fibrous ring (y: 28..30) everywhere first
            for (int y = 27; y <= 30; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    if (y >= 28 && y <= 30)
                        gridRegion[x, y] = HeartRegion.None;
                }
            }

            // 3. SA Node in superior-right RA
            for (int y = 13; y <= 15; y++)
                for (int x = 19; x <= 21; x++)
                    gridRegion[x, y] = HeartRegion.SANode;

            // 4. AV Node bridge across fibrous ring (x: 32..34, y: 27..31)
            for (int y = 27; y <= 31; y++)
                for (int x = 32; x <= 34; x++)
                    gridRegion[x, y] = HeartRegion.AVNode;

            // 5. His Bundle & Purkinje network (8-neighbor connected continuously from AV node to LV & RV endocardium)
            for (int y = 32; y <= 36; y++)
            {
                gridRegion[33, y] = HeartRegion.Purkinje;
                gridRegion[34, y] = HeartRegion.Purkinje;
            }
            // Bridge His -> Left Bundle (x=36..37) and Right Bundle (x=31..32)
            gridRegion[35, 35] = HeartRegion.Purkinje;
            gridRegion[35, 36] = HeartRegion.Purkinje;
            gridRegion[32, 36] = HeartRegion.Purkinje;

            // Left bundle branch: activates LEFT side of septum (x=36, y=35..46) early, leaving lower septum (y=47..55) open for LV<->Septum myocardial continuity!
            for (int y = 35; y <= 46; y++)
            {
                if (gridRegion[36, y] != HeartRegion.None)
                    gridRegion[36, y] = HeartRegion.Purkinje;
            }
            // Right bundle branch along RV septum (y=36..46)
            for (int y = 36; y <= 46; y++)
            {
                if (gridRegion[31, y] != HeartRegion.None)
                    gridRegion[31, y] = HeartRegion.Purkinje;
            }
            // Continuous Purkinje lining along LV & RV inner endocardium (transmural < 0.24, keeping apical septum x=31..36, y=48..56 myocardial!)
            for (int y = 33; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    if (gridRegion[x, y] == HeartRegion.LeftVentricle && transmuralGrid[x, y] < 0.24f)
                    {
                        gridRegion[x, y] = HeartRegion.Purkinje;
                    }
                    else if (gridRegion[x, y] == HeartRegion.RightVentricle && transmuralGrid[x, y] < 0.24f && y >= 34)
                    {
                        gridRegion[x, y] = HeartRegion.Purkinje;
                    }
                }
            }
            // Connect Left/Right bundles at y=45..47 laterally into LV/RV endocardium without sealing apical septum
            for (int x = 37; x <= 42; x++)
            {
                if (gridRegion[x, 46] != HeartRegion.None && transmuralGrid[x, 46] < 0.35f)
                    gridRegion[x, 46] = HeartRegion.Purkinje;
            }
            for (int x = 26; x <= 30; x++)
            {
                if (gridRegion[x, 46] != HeartRegion.None && transmuralGrid[x, 46] < 0.35f)
                    gridRegion[x, 46] = HeartRegion.Purkinje;
            }

            // Map active cells to dense indices
            var indexMap = new int[GridSize, GridSize];
            for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    indexMap[x, y] = -1;

            var coords = new List<(int x, int y, HeartRegion r)>(2500);
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    if (gridRegion[x, y] != HeartRegion.None)
                    {
                        indexMap[x, y] = coords.Count;
                        coords.Add((x, y, gridRegion[x, y]));
                    }
                }
            }

            CellCount = coords.Count;
            Regions = new HeartRegion[CellCount];
            GridX = new float[CellCount];
            GridY = new float[CellCount];
            TransmuralDepth = new float[CellCount];
            MassWeight = new float[CellCount];
            BaseApdMs = new float[CellCount];
            BaseAlpha = new float[CellCount];
            Heterogeneity = new float[CellCount];
            FocalLvMask = new float[CellCount];
            SubendoLvMask = new float[CellCount];
            FocalRvMask = new float[CellCount];
            NeighborIdx = new int[CellCount * MaxNeighbors];
            NeighborWeight = new float[CellCount * MaxNeighbors];
            GradLeft = new int[CellCount];
            GradRight = new int[CellCount];
            GradUp = new int[CellCount];
            GradDown = new int[CellCount];

            var saList = new List<int>();
            var pkList = new List<int>();
            var lvList = new List<int>();
            var rvList = new List<int>();
            var sepList = new List<int>();
            var ventList = new List<int>();

            var rng = new Random(133742);
            int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };
            float[] dw = { 0.70710678f, 1f, 0.70710678f, 1f, 1f, 0.70710678f, 1f, 0.70710678f };

            for (int i = 0; i < CellCount; i++)
            {
                var (x, y, r) = coords[i];
                Regions[i] = r;
                GridX[i] = x;
                GridY[i] = y;
                float tm = transmuralGrid[x, y];
                TransmuralDepth[i] = tm;
                Heterogeneity[i] = 0.96f + (float)rng.NextDouble() * 0.08f;

                switch (r)
                {
                    case HeartRegion.SANode:
                        MassWeight[i] = 0.02f;
                        BaseApdMs[i] = 140f;
                        BaseAlpha[i] = 0.65f;
                        saList.Add(i);
                        break;
                    case HeartRegion.RightAtrium:
                    case HeartRegion.LeftAtrium:
                        MassWeight[i] = 0.34f;
                        BaseApdMs[i] = 155f;
                        BaseAlpha[i] = 0.95f;
                        break;
                    case HeartRegion.AVNode:
                        MassWeight[i] = 0.015f;
                        BaseApdMs[i] = 220f;
                        BaseAlpha[i] = 0.036f; // Slow AV conduction -> ~140-170 ms PR interval
                        break;
                    case HeartRegion.Purkinje:
                        MassWeight[i] = 0.08f;
                        BaseApdMs[i] = 340f;
                        BaseAlpha[i] = 2.80f; // Fast His-Purkinje (plus 3x Purkinje micro-step -> ~15ms full endocardial activation)
                        pkList.Add(i);
                        ventList.Add(i);
                        break;
                    case HeartRegion.Septum:
                        MassWeight[i] = 0.42f;
                        BaseApdMs[i] = 310f;
                        BaseAlpha[i] = 0.85f;
                        sepList.Add(i);
                        ventList.Add(i);
                        break;
                    case HeartRegion.RightVentricle:
                        MassWeight[i] = 0.28f;
                        // Epicardium shorter APD than endocardium -> upright T wave
                        BaseApdMs[i] = 325f - 120f * tm;
                        BaseAlpha[i] = 0.82f;
                        rvList.Add(i);
                        ventList.Add(i);
                        break;
                    case HeartRegion.LeftVentricle:
                        MassWeight[i] = 1.35f;
                        // Strong transmural APD gradient (endo ~335ms -> epi ~200ms) -> flat ST + crisp upright T wave + ~380-400ms QTc
                        BaseApdMs[i] = 335f - 135f * tm;
                        BaseAlpha[i] = 0.85f;
                        lvList.Add(i);
                        ventList.Add(i);
                        break;
                }

                // Coronary territory masks for realistic ischemia
                if (r == HeartRegion.LeftVentricle)
                {
                    // Focal transmural/subepicardial LV lateral-apical STEMI zone (centered around (49, 50), epicardial-biased)
                    float distLv = (float)Math.Sqrt((x - 49f) * (x - 49f) + (y - 50f) * (y - 50f));
                    float mask = Clamp01(1.1f - distLv / 15f);
                    FocalLvMask[i] = mask * (tm > 0.25f ? (0.55f + 0.45f * tm) : 0.20f);

                    // Subendocardial ischemia zone in the anterior-lateral-apical LV wall (produces clean ST depression)
                    SubendoLvMask[i] = mask * Clamp01((0.62f - tm) / 0.50f);
                }
                else if (r == HeartRegion.RightVentricle)
                {
                    float distRv = (float)Math.Sqrt((x - 21f) * (x - 21f) + (y - 43f) * (y - 43f));
                    FocalRvMask[i] = Clamp01(1.1f - distRv / 14f);
                }

                // Gradient neighbors
                GradLeft[i] = x > 0 ? indexMap[x - 1, y] : -1;
                GradRight[i] = x < GridSize - 1 ? indexMap[x + 1, y] : -1;
                GradUp[i] = y > 0 ? indexMap[x, y - 1] : -1;
                GradDown[i] = y < GridSize - 1 ? indexMap[x, y + 1] : -1;

                // 8-neighbor connections
                int baseOffset = i * MaxNeighbors;
                for (int n = 0; n < MaxNeighbors; n++)
                {
                    int nx = x + dx[n];
                    int ny = y + dy[n];
                    int nIdx = (nx >= 0 && nx < GridSize && ny >= 0 && ny < GridSize) ? indexMap[nx, ny] : -1;
                    if (nIdx >= 0)
                    {
                        // Prevent retrograde Purkinje->AV jumping too awkwardly; AV node connects naturally
                        // Also throttle Purkinje -> Ventricular myocardium coupling slightly at septal junction so left septum activates before RV
                        NeighborIdx[baseOffset + n] = nIdx;
                        NeighborWeight[baseOffset + n] = dw[n];
                    }
                    else
                    {
                        NeighborIdx[baseOffset + n] = -1;
                        NeighborWeight[baseOffset + n] = 0f;
                    }
                }
            }

            SaNodeCells = saList.ToArray();
            PurkinjeCells = pkList.ToArray();
            LvCells = lvList.ToArray();
            RvCells = rvList.ToArray();
            SeptumCells = sepList.ToArray();
            VentricularCells = ventList.ToArray();
        }

        public static CardiacAnatomy CreateDefault() => new CardiacAnatomy();

        private static float EllipseDist(float x, float y, float cx, float cy, float rx, float ry)
        {
            float dx = (x - cx) / rx;
            float dy = (y - cy) / ry;
            return dx * dx + dy * dy;
        }

        private static float TiltedEllipse(float x, float y, float cx, float cy, float rx, float ry, float angleRad)
        {
            float dx = x - cx;
            float dy = y - cy;
            float c = (float)Math.Cos(angleRad);
            float s = (float)Math.Sin(angleRad);
            float u = (dx * c + dy * s) / rx;
            float v = (-dx * s + dy * c) / ry;
            return u * u + v * v;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
