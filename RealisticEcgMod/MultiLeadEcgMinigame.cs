using System;
using System.Collections.Generic;
using CardiacSimCore;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RealisticEcgMod
{
    /// <summary>
    /// 6-lead frontal plane ECG machine Minigame (I, II, III, aVR, aVL, aVF):
    /// - Starts sweeping left-to-right from the left margin on open (like bedside ICU monitor / vanilla ECGVisualizer).
    /// - Uses standard calibration grid (small square = 0.04s x 0.1mV, large square = 0.2s x 0.5mV at 25mm/s &amp; 10mm/mV).
    /// - Changing Paper Speed or Gain preserves all already-drawn historical waveforms and only affects new sweeps ahead of the beam.
    /// </summary>
    public sealed class MultiLeadEcgMinigame : Minigame
    {
        private const int PixelsPerMm = 5;    // 1 mm small square = 5 px, 5 mm large square = 25 px
        private const int GridWidth = 750;    // 150 mm wide
        private const int GridHeight = 450;   // 90 mm tall (75 px / 15 mm per lead across 6 leads)
        private const int TraceStartX = 24;   // Left 24 px reserved for 1 mV calibration square pulse
        private const int TraceSpanX = GridWidth - TraceStartX;
        private const int EraserGapCols = 8;

        private static readonly float[] PaperSpeedsMmPerSec = { 12.5f, 25.0f, 50.0f };
        private static readonly float[] GainsMmPerMv = { 5.0f, 10.0f, 20.0f };
        private static readonly FrontalLead[] LeadOrder =
        {
            FrontalLead.I,
            FrontalLead.II,
            FrontalLead.III,
            FrontalLead.aVR,
            FrontalLead.aVL,
            FrontalLead.aVF
        };

        private readonly Body _body;
        private readonly bool _openedWithItem;

        private GameObject? _rootGo;
        private Texture2D? _tex;
        private Color32[] _gridBase = new Color32[0];
        private Color32[] _traceLayer = new Color32[0]; // Persistent drawn trace alpha/RGB overlay (preserved on gain/speed change)
        private Color32[] _frameBuf = new Color32[0];

        private readonly int[] _prevY = new int[6];
        private int _writeCol; // 0 .. TraceSpanX-1 (starts at 0 = left edge!)
        private float _timeAccum;
        private bool _needComposite = true;

        private int _speedIndex = 1; // 25 mm/s
        private int _gainIndex = 1;  // 10 mm/mV
        private bool _frozen;

        private RectTransform? _speedBtnRect;
        private RectTransform? _gainBtnRect;
        private RectTransform? _freezeBtnRect;
        private RectTransform? _closeBtnRect;

        private TextMeshProUGUI? _rhythmText;
        private TextMeshProUGUI? _vitalsText;
        private TextMeshProUGUI? _calibText;
        private TextMeshProUGUI? _speedBtnText;
        private TextMeshProUGUI? _gainBtnText;
        private TextMeshProUGUI? _freezeBtnText;

        public MultiLeadEcgMinigame(Body body, bool openedWithItem)
        {
            _body = body;
            _openedWithItem = openedWithItem;
        }

        public override HandSpriteType HandType() => HandSpriteType.Point;

        public override string GuideLocaleString() => "multileadEcgGuide";

        public override bool NeedsItem() => _openedWithItem;

        public override bool CanExit() => true;

        public override void Start()
        {
            TMP_FontAsset? sharedFont = ResolveSharedFont();

            var root = new GameObject("MultiLeadEcgScreen", typeof(RectTransform), typeof(Image));
            root.transform.SetParent(Minigame.game.minigameScreen, false);
            root.transform.SetSiblingIndex(0);
            Minigame.game.spawnedMiniGame = root.transform;
            _rootGo = root;

            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.anchoredPosition = Vector2.zero;
            rootRt.sizeDelta = new Vector2(880f, 575f);

            var rootBg = root.GetComponent<Image>();
            rootBg.color = new Color(0.08f, 0.11f, 0.13f, 0.97f);

            CreatePanel(root.transform, "Bezel", new Vector2(0f, 2f), new Vector2(852f, 545f), new Color(0.03f, 0.05f, 0.06f, 1f));

            _rhythmText = CreateText(root.transform, "RhythmLabel", sharedFont, new Vector2(-215f, 246f), new Vector2(400f, 34f),
                TextAlignmentOptions.Left, 20f, new Color(0.32f, 1f, 0.58f, 1f));
            _vitalsText = CreateText(root.transform, "VitalsLabel", sharedFont, new Vector2(215f, 246f), new Vector2(390f, 34f),
                TextAlignmentOptions.Right, 18f, new Color(0.86f, 0.96f, 1f, 1f));
            _calibText = CreateText(root.transform, "CalibLegend", sharedFont, new Vector2(0f, 218f), new Vector2(820f, 22f),
                TextAlignmentOptions.Left, 13.5f, new Color(0.72f, 0.85f, 0.78f, 0.95f));

            _tex = new Texture2D(GridWidth, GridHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            BuildGridBackground();

            // Initialize sweep position at leftmost edge & each lead's baseline
            _writeCol = 0;
            _timeAccum = 0f;
            int rowH = GridHeight / LeadOrder.Length;
            for (int r = 0; r < LeadOrder.Length; r++)
            {
                int invRow = LeadOrder.Length - 1 - r;
                int rowBottom = invRow * rowH;
                _prevY[r] = rowBottom + Mathf.RoundToInt(rowH * 0.42f);
            }

            var graphGo = new GameObject("EcgPaperGrid", typeof(RectTransform), typeof(RawImage));
            graphGo.transform.SetParent(root.transform, false);
            var graphRt = graphGo.GetComponent<RectTransform>();
            graphRt.anchorMin = new Vector2(0.5f, 0.5f);
            graphRt.anchorMax = new Vector2(0.5f, 0.5f);
            graphRt.pivot = new Vector2(0.5f, 0.5f);
            graphRt.anchoredPosition = new Vector2(32f, -12f);
            graphRt.sizeDelta = new Vector2(GridWidth, GridHeight);
            graphGo.GetComponent<RawImage>().texture = _tex;

            float rowHeight = GridHeight / 6f;
            float topY = -12f + GridHeight * 0.5f;
            for (int r = 0; r < LeadOrder.Length; r++)
            {
                float cy = topY - (r + 0.5f) * rowHeight;
                CreateText(root.transform, $"LeadLabel_{r}", sharedFont,
                    new Vector2(-380f, cy), new Vector2(56f, 28f),
                    TextAlignmentOptions.Center, 16f, new Color(0.55f, 1f, 0.72f, 1f),
                    CardiacModManager.GetFrontalLeadLabel(LeadOrder[r]));
            }

            float ctrlY = -260f;
            _speedBtnRect = CreateButton(root.transform, "SpeedBtn", sharedFont, new Vector2(-285f, ctrlY), new Vector2(185f, 34f),
                new Color(0.12f, 0.22f, 0.26f, 1f), out _speedBtnText);
            _gainBtnRect = CreateButton(root.transform, "GainBtn", sharedFont, new Vector2(-80f, ctrlY), new Vector2(175f, 34f),
                new Color(0.12f, 0.22f, 0.26f, 1f), out _gainBtnText);
            _freezeBtnRect = CreateButton(root.transform, "FreezeBtn", sharedFont, new Vector2(125f, ctrlY), new Vector2(165f, 34f),
                new Color(0.18f, 0.20f, 0.14f, 1f), out _freezeBtnText);
            _closeBtnRect = CreateButton(root.transform, "CloseBtn", sharedFont, new Vector2(325f, ctrlY), new Vector2(140f, 34f),
                new Color(0.28f, 0.14f, 0.14f, 1f), out var closeText);
            if (closeText != null) closeText.text = "关闭 [ESC]";

            UpdateControlLabels();
            CompositeAndUpload();
        }

        public override void Update(List<RaycastResult> uiCasts)
        {
            if (_rootGo == null || _tex == null) return;

            if (Minigame.game.handStartedClicking && uiCasts != null)
            {
                foreach (RaycastResult hit in uiCasts)
                {
                    GameObject go = hit.gameObject;
                    if (_speedBtnRect != null && (go == _speedBtnRect.gameObject || go.transform.IsChildOf(_speedBtnRect)))
                    {
                        _speedIndex = (_speedIndex + 1) % PaperSpeedsMmPerSec.Length;
                        // Keep existing _traceLayer intact; only adjust new step time!
                        UpdateControlLabels();
                        _needComposite = true;
                        break;
                    }
                    if (_gainBtnRect != null && (go == _gainBtnRect.gameObject || go.transform.IsChildOf(_gainBtnRect)))
                    {
                        _gainIndex = (_gainIndex + 1) % GainsMmPerMv.Length;
                        // Keep existing _traceLayer intact; only adjust new amplitude & left 1mV pulse!
                        UpdateControlLabels();
                        _needComposite = true;
                        break;
                    }
                    if (_freezeBtnRect != null && (go == _freezeBtnRect.gameObject || go.transform.IsChildOf(_freezeBtnRect)))
                    {
                        _frozen = !_frozen;
                        UpdateControlLabels();
                        break;
                    }
                    if (_closeBtnRect != null && (go == _closeBtnRect.gameObject || go.transform.IsChildOf(_closeBtnRect)))
                    {
                        Minigame.game.EndMinigame();
                        return;
                    }
                }
            }

            UpdateVitalsHeader();

            if (!_frozen)
            {
                StepLeftToRightSweep();
            }

            if (_needComposite)
            {
                CompositeAndUpload();
                _needComposite = false;
            }
        }

        private void UpdateControlLabels()
        {
            float speed = PaperSpeedsMmPerSec[_speedIndex];
            float gain = GainsMmPerMv[_gainIndex];
            float secPerSmallBox = 1.0f / speed;       // 0.04s at 25 mm/s
            float secPerLargeBox = 5.0f * secPerSmallBox;
            float mvPerSmallBox = 1.0f / gain;         // 0.1 mV at 10 mm/mV
            float mvPerLargeBox = 5.0f * mvPerSmallBox;

            if (_speedBtnText != null) _speedBtnText.text = $"走纸: {speed:0.#} mm/s";
            if (_gainBtnText != null) _gainBtnText.text = $"增益: {gain:0.#} mm/mV";
            if (_freezeBtnText != null) _freezeBtnText.text = _frozen ? "状态: 已冻结 (继续)" : "状态: 实时 (冻结)";
            if (_calibText != null)
            {
                _calibText.text =
                    $"标准网格: 小格 {secPerSmallBox:0.###}s × {mvPerSmallBox:0.##}mV  |  大格 {secPerLargeBox:0.##}s × {mvPerLargeBox:0.##}mV   (25mm/s=0.04s/0.2s, 10mm/mV=0.1mV/0.5mV)";
            }
        }

        private void UpdateVitalsHeader()
        {
            var ctrl = CardiacModManager.GetOrCreate(_body);
            CardiacTelemetrySnapshot snap = ctrl.Worker.GetTelemetrySnapshot();
            ctrl.LastSnapshot = snap;

            if (_rhythmText != null)
            {
                _rhythmText.text = "心律: " + CardiacModManager.GetRhythmDisplayName(in snap);
                _rhythmText.color = snap.Rhythm switch
                {
                    RhythmType.VentricularFibrillation or RhythmType.Asystole => new Color(1f, 0.35f, 0.35f, 1f),
                    RhythmType.VentricularTachycardia => new Color(1f, 0.65f, 0.25f, 1f),
                    _ => snap.HasRecentPvc ? new Color(1f, 0.92f, 0.35f, 1f) : new Color(0.35f, 1f, 0.62f, 1f)
                };
            }

            if (_vitalsText != null)
            {
                float hr = (snap.Rhythm == RhythmType.VentricularFibrillation || snap.Rhythm == RhythmType.Asystole)
                    ? 0f
                    : snap.HeartRateBpm;
                string bpStr = !WorldGeneration.unchipped ? _body.bloodPressureReadout : "--/--";
                _vitalsText.text = $"HR: {Mathf.RoundToInt(hr)} bpm   BP: {bpStr}";
            }
        }

        private void StepLeftToRightSweep()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            float speed = PaperSpeedsMmPerSec[_speedIndex];
            float gain = GainsMmPerMv[_gainIndex];
            float colDt = 1.0f / (speed * PixelsPerMm); // Seconds per 1 px column (0.008s @ 25 mm/s, 0.004s @ 50 mm/s)

            _timeAccum = Mathf.Min(_timeAccum + dt, 1.5f);
            if (_timeAccum < colDt) return;

            var ctrl = CardiacModManager.GetOrCreate(_body);
            int rowH = GridHeight / LeadOrder.Length;

            while (_timeAccum >= colDt)
            {
                _timeAccum -= colDt;
                int wx = TraceStartX + _writeCol;

                // Erase narrow sweep-bar gap immediately ahead of the moving beam
                for (int g = 1; g <= EraserGapCols; g++)
                {
                    int clearX = TraceStartX + ((_writeCol + g) % TraceSpanX);
                    for (int cy = 0; cy < GridHeight; cy++)
                    {
                        _traceLayer[cy * GridWidth + clearX].a = 0;
                    }
                }

                // Sample synchronized 6-lead electrical projection at current beam lag
                FrontalEcgSample s = ctrl.Worker.SampleSynchronizedPeakPreserving(_timeAccum, colDt);

                for (int r = 0; r < LeadOrder.Length; r++)
                {
                    int invRow = LeadOrder.Length - 1 - r;
                    int rowBottom = invRow * rowH;
                    int rowTop = rowBottom + rowH - 1;
                    int baseY = rowBottom + Mathf.RoundToInt(rowH * 0.42f);

                    float v = s.GetLead(LeadOrder[r]);
                    int y = Mathf.Clamp(baseY + Mathf.RoundToInt(v * gain * PixelsPerMm), rowBottom + 1, rowTop - 1);

                    // Clear current column in this row before painting new segment cleanly
                    for (int cy = rowBottom; cy <= rowTop; cy++)
                    {
                        _traceLayer[cy * GridWidth + wx].a = 0;
                    }

                    DrawTraceColumn(_traceLayer, wx, _prevY[r], y, rowBottom + 1, rowTop - 1);
                    _prevY[r] = y;
                }

                _writeCol++;
                if (_writeCol >= TraceSpanX)
                    _writeCol = 0;

                _needComposite = true;
            }
        }

        private void CompositeAndUpload()
        {
            if (_tex == null) return;

            Array.Copy(_gridBase, _frameBuf, _gridBase.Length);

            // Overlay persistent trace pixels onto paper grid
            for (int p = 0; p < _frameBuf.Length; p++)
            {
                byte a = _traceLayer[p].a;
                if (a == 255)
                {
                    _frameBuf[p] = _traceLayer[p];
                }
                else if (a > 0)
                {
                    int invA = 255 - a;
                    _frameBuf[p].r = (byte)((_traceLayer[p].r * a + _frameBuf[p].r * invA) >> 8);
                    _frameBuf[p].g = (byte)((_traceLayer[p].g * a + _frameBuf[p].g * invA) >> 8);
                    _frameBuf[p].b = (byte)((_traceLayer[p].b * a + _frameBuf[p].b * invA) >> 8);
                    _frameBuf[p].a = 255;
                }
            }

            // Draw subtle glowing sweep cursor line at active beam position
            int cursorX = TraceStartX + _writeCol;
            var cursorColor = new Color32(160, 255, 200, 255);
            for (int y = 0; y < GridHeight; y++)
            {
                if ((y & 3) == 0)
                    _frameBuf[y * GridWidth + cursorX] = cursorColor;
            }

            // Draw live 1 mV calibration square pulses in dedicated left margin (cols 0..TraceStartX-1)
            float gain = GainsMmPerMv[_gainIndex];
            int calHeightPx = Mathf.RoundToInt(1.0f * gain * PixelsPerMm);
            int rowH = GridHeight / LeadOrder.Length;
            for (int r = 0; r < LeadOrder.Length; r++)
            {
                int invRow = LeadOrder.Length - 1 - r;
                int rowBottom = invRow * rowH;
                int rowTop = rowBottom + rowH - 1;
                int baseY = rowBottom + Mathf.RoundToInt(rowH * 0.42f);
                DrawCalibrationPulse(_frameBuf, baseY, rowBottom + 2, rowTop - 2, calHeightPx);
            }

            _tex.SetPixels32(_frameBuf);
            _tex.Apply(false);
        }

        private static void DrawCalibrationPulse(Color32[] dst, int baseY, int minY, int maxY, int heightPx)
        {
            var calColor = new Color32(140, 235, 175, 255);
            int topY = Mathf.Clamp(baseY + heightPx, minY, maxY);
            DrawHorizSegment(dst, 4, 9, baseY, calColor);
            DrawVertSegment(dst, 9, baseY, topY, calColor);
            DrawHorizSegment(dst, 9, 17, topY, calColor);
            DrawVertSegment(dst, 17, baseY, topY, calColor);
            DrawHorizSegment(dst, 17, 22, baseY, calColor);
        }

        private static void DrawTraceColumn(Color32[] layer, int x, int y0, int y1, int minY, int maxY)
        {
            int low = Mathf.Min(y0, y1);
            int high = Mathf.Max(y0, y1);
            var bright = new Color32(115, 255, 155, 255);
            var softEdge = new Color32(90, 225, 130, 145);

            for (int py = low; py <= high; py++)
            {
                layer[py * GridWidth + x] = bright;
            }
            if (low - 1 >= minY && layer[(low - 1) * GridWidth + x].a < 145)
            {
                layer[(low - 1) * GridWidth + x] = softEdge;
            }
            if (high + 1 <= maxY && layer[(high + 1) * GridWidth + x].a < 145)
            {
                layer[(high + 1) * GridWidth + x] = softEdge;
            }
        }

        private static void DrawHorizSegment(Color32[] dst, int x0, int x1, int y, Color32 c)
        {
            if (y < 0 || y >= GridHeight) return;
            for (int x = Mathf.Max(0, x0); x <= Mathf.Min(GridWidth - 1, x1); x++)
                dst[y * GridWidth + x] = c;
        }

        private static void DrawVertSegment(Color32[] dst, int x, int y0, int y1, Color32 c)
        {
            if (x < 0 || x >= GridWidth) return;
            int low = Mathf.Max(0, Mathf.Min(y0, y1));
            int high = Mathf.Min(GridHeight - 1, Mathf.Max(y0, y1));
            for (int y = low; y <= high; y++)
                dst[y * GridWidth + x] = c;
        }

        private void BuildGridBackground()
        {
            _gridBase = new Color32[GridWidth * GridHeight];
            _traceLayer = new Color32[GridWidth * GridHeight];
            _frameBuf = new Color32[GridWidth * GridHeight];

            var bg = new Color32(10, 22, 16, 255);
            var minorLine = new Color32(20, 48, 34, 255); // 1 mm small square (0.04 s x 0.1 mV)
            var majorLine = new Color32(40, 92, 64, 255); // 5 mm large square (0.20 s x 0.5 mV)
            var rowSep = new Color32(72, 128, 96, 255);

            int largeStep = PixelsPerMm * 5; // 25 px
            int rowH = GridHeight / LeadOrder.Length;

            for (int y = 0; y < GridHeight; y++)
            {
                bool yMajor = (y % largeStep) == 0;
                bool yMinor = (y % PixelsPerMm) == 0;
                bool isRowBorder = y > 0 && (y % rowH) == 0;

                for (int x = 0; x < GridWidth; x++)
                {
                    if (isRowBorder)
                    {
                        _gridBase[y * GridWidth + x] = rowSep;
                        continue;
                    }
                    bool xMajor = (x % largeStep) == 0;
                    bool xMinor = (x % PixelsPerMm) == 0;

                    if (xMajor || yMajor)
                        _gridBase[y * GridWidth + x] = majorLine;
                    else if (xMinor || yMinor)
                        _gridBase[y * GridWidth + x] = minorLine;
                    else
                        _gridBase[y * GridWidth + x] = bg;
                }
            }
        }

        private static TMP_FontAsset? ResolveSharedFont()
        {
            if (WoundView.view != null && WoundView.view.bpmText != null)
                return WoundView.view.bpmText.font;
            if (PlayerCamera.main != null && PlayerCamera.main.consciousnessText != null)
                return PlayerCamera.main.consciousnessText.font;
            return TMP_Settings.defaultFontAsset;
        }

        private static void CreatePanel(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private static TextMeshProUGUI CreateText(
            Transform parent,
            string name,
            TMP_FontAsset? font,
            Vector2 pos,
            Vector2 size,
            TextAlignmentOptions align,
            float fontSize,
            Color color,
            string initialText = "")
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = fontSize;
            tmp.alignment = align;
            tmp.color = color;
            tmp.text = initialText;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static RectTransform CreateButton(
            Transform parent,
            string name,
            TMP_FontAsset? font,
            Vector2 pos,
            Vector2 size,
            Color bgColor,
            out TextMeshProUGUI? label)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var img = go.GetComponent<Image>();
            img.color = bgColor;
            img.raycastTarget = true;

            label = CreateText(go.transform, "Label", font, Vector2.zero, size - new Vector2(8f, 4f),
                TextAlignmentOptions.Center, 14f, Color.white);
            return rt;
        }
    }
}
