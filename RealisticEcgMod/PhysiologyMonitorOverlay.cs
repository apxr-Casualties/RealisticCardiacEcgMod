using System;
using System.Collections.Generic;
using System.Linq;
using CardiacSimCore;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RealisticEcgMod
{
    /// <summary>
    /// Descriptor for a single real-time physiological waveform channel displayed inside
    /// the draggable multi-curve monitor overlay. External mods (e.g. respiration, neurology)
    /// can register their own channels via <see cref="PhysiologyMonitorApi"/>.
    /// </summary>
    public sealed class MonitorCurveDescriptor
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public Color TraceColor { get; set; } = new Color(1f, 0.34f, 0.34f, 1f);
        public float MinValue { get; set; } = 0f;
        public float MaxValue { get; set; } = 100f;
        public float[] ReferenceLines { get; set; } = Array.Empty<float>();
        public float SweepSpanSeconds { get; set; } = 5.0f;
        public int SortOrder { get; set; } = 100;

        /// <summary>
        /// High-resolution buffered sampler: (body, offsetSecAgo, windowSec) -> physiological value.
        /// Preferred for fast waveforms (ABP, airflow, capnography).
        /// </summary>
        public Func<Body, float, float, float>? SampleHistory { get; set; }

        /// <summary>
        /// Simple instantaneous sampler: (body) -> current value.
        /// Used automatically if <see cref="SampleHistory"/> is null.
        /// </summary>
        public Func<Body, float>? SampleCurrent { get; set; }

        /// <summary>
        /// Optional live readout formatter displayed in the channel header (e.g. "120/80 (93) mmHg").
        /// </summary>
        public Func<Body, string>? FormatReadout { get; set; }
    }

    /// <summary>
    /// Public cross-mod API for registering, unregistering, and toggling curves on the
    /// biochip real-time physiology monitor overlay.
    /// </summary>
    public static class PhysiologyMonitorApi
    {
        public const string ArterialPressureCurveId = "cardiac.abp";

        private static readonly object LockObj = new object();
        private static readonly Dictionary<string, MonitorCurveDescriptor> Curves =
            new Dictionary<string, MonitorCurveDescriptor>(StringComparer.OrdinalIgnoreCase);

        // Per-curve hidden state is intentionally stored in memory only (not persisted to disk)
        private static readonly HashSet<string> HiddenCurveIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static event Action? OnCurvesChanged;

        public static void RegisterCurve(MonitorCurveDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Id))
                throw new ArgumentException("MonitorCurveDescriptor must have a non-empty Id.", nameof(descriptor));

            lock (LockObj)
            {
                Curves[descriptor.Id.Trim()] = descriptor;
            }
            OnCurvesChanged?.Invoke();
        }

        /// <summary>
        /// Convenience &amp; reflection-friendly overload for third-party mods that only want to pass
        /// a simple current-value callback without referencing <see cref="MonitorCurveDescriptor"/>.
        /// </summary>
        public static void RegisterSimpleCurve(
            string id,
            string displayName,
            string unit,
            Color traceColor,
            float minValue,
            float maxValue,
            float[]? referenceLines,
            Func<Body, float> sampleCurrent,
            Func<Body, string>? formatReadout = null,
            int sortOrder = 100,
            float sweepSpanSeconds = 5.0f)
        {
            if (sampleCurrent == null)
                throw new ArgumentNullException(nameof(sampleCurrent));

            RegisterCurve(new MonitorCurveDescriptor
            {
                Id = id,
                DisplayName = displayName,
                Unit = unit ?? string.Empty,
                TraceColor = traceColor,
                MinValue = minValue,
                MaxValue = maxValue,
                ReferenceLines = referenceLines ?? Array.Empty<float>(),
                SampleCurrent = sampleCurrent,
                FormatReadout = formatReadout,
                SortOrder = sortOrder,
                SweepSpanSeconds = Mathf.Clamp(sweepSpanSeconds, 1.5f, 20f)
            });
        }

        public static bool UnregisterCurve(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            bool removed;
            lock (LockObj)
            {
                string key = id.Trim();
                removed = Curves.Remove(key);
                HiddenCurveIds.Remove(key);
            }
            if (removed)
                OnCurvesChanged?.Invoke();
            return removed;
        }

        public static void SetCurveVisible(string id, bool visible)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            bool changed;
            lock (LockObj)
            {
                string key = id.Trim();
                changed = visible ? HiddenCurveIds.Remove(key) : HiddenCurveIds.Add(key);
            }
            if (changed)
                OnCurvesChanged?.Invoke();
        }

        public static bool IsCurveVisible(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            lock (LockObj)
            {
                return !HiddenCurveIds.Contains(id.Trim());
            }
        }

        public static IReadOnlyList<string> GetRegisteredCurveIds()
        {
            lock (LockObj)
            {
                return Curves.Values
                    .OrderBy(c => c.SortOrder)
                    .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(c => c.Id)
                    .ToList();
            }
        }

        internal static List<MonitorCurveDescriptor> GetOrderedCurvesSnapshot()
        {
            lock (LockObj)
            {
                return Curves.Values
                    .OrderBy(c => c.SortOrder)
                    .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        internal static void RegisterBuiltInArterialPressureCurve()
        {
            RegisterCurve(new MonitorCurveDescriptor
            {
                Id = ArterialPressureCurveId,
                DisplayName = "动脉压 ABP",
                Unit = "mmHg",
                TraceColor = new Color(1.0f, 0.30f, 0.32f, 1.0f),
                MinValue = 0f,
                MaxValue = 250f,
                ReferenceLines = new[] { 50f, 100f, 150f, 200f },
                SweepSpanSeconds = 5.0f,
                SortOrder = 0,
                SampleHistory = CardiacModManager.SampleArterialPressureForMonitor,
                SampleCurrent = body =>
                {
                    if (body == null || !body.alive) return 0f;
                    var ctrl = CardiacModManager.GetOrCreate(body);
                    return ctrl.LastSnapshot.CurrentArterialPressureMmHg;
                },
                FormatReadout = body =>
                {
                    if (body == null || !body.alive) return "0/0 (0) mmHg";
                    var ctrl = CardiacModManager.GetOrCreate(body);
                    ref readonly var snap = ref ctrl.LastSnapshot;
                    int sbp = Mathf.RoundToInt(snap.SystolicBp5s);
                    int dbp = Mathf.RoundToInt(snap.DiastolicBp5s);
                    int map = Mathf.RoundToInt(snap.MeanArterialPressure5s);
                    return $"{sbp}/{dbp} ({map}) mmHg";
                }
            });
        }
    }

    internal sealed class MonitorDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RectTransform? TargetWindow;
        public RectTransform? BoundsRect;
        private Vector2 _dragOffset;

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (TargetWindow == null || BoundsRect == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    BoundsRect, eventData.position, eventData.pressEventCamera, out Vector2 localPoint))
            {
                _dragOffset = TargetWindow.anchoredPosition - localPoint;
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (TargetWindow == null || BoundsRect == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    BoundsRect, eventData.position, eventData.pressEventCamera, out Vector2 localPoint))
            {
                TargetWindow.anchoredPosition = ClampToParent(localPoint + _dragOffset);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (TargetWindow == null) return;
            TargetWindow.anchoredPosition = ClampToParent(TargetWindow.anchoredPosition);
            RealisticEcgPlugin.SaveMonitorOverlayPosition(TargetWindow.anchoredPosition);
        }

        public Vector2 ClampToParent(Vector2 pos)
        {
            if (TargetWindow == null || BoundsRect == null) return pos;
            Rect parentRect = BoundsRect.rect;
            if (parentRect.width < 100f || parentRect.height < 100f) return pos;
            Vector2 size = TargetWindow.rect.size;
            if (size.x <= 1f || size.y <= 1f) size = TargetWindow.sizeDelta;

            // Window uses anchor (1, 1), pivot (1, 1) inside full-screen BoundsRect -> (0, 0) is top-right of screen
            float minX = -(parentRect.width - size.x - 8f);
            float maxX = -8f;
            float minY = -(parentRect.height - size.y - 8f);
            float maxY = -8f;

            if (minX > maxX) minX = maxX = -8f;
            if (minY > maxY) minY = maxY = -8f;

            return new Vector2(Mathf.Clamp(pos.x, minX, maxX), Mathf.Clamp(pos.y, minY, maxY));
        }
    }

    /// <summary>
    /// Draggable, non-persistent biochip multi-curve monitor overlay.
    /// Automatically hides when WoundView is closed, unchipped, or body dies.
    /// </summary>
    internal sealed class PhysiologyMonitorOverlay : MonoBehaviour
    {
        private const int PlotTexWidth = 380;
        private const int PlotTexHeight = 114;
        private const float WindowWidth = 420f;
        private const float HeaderHeight = 28f;
        private const float ToggleBarHeight = 26f;
        private const float ChannelBlockHeight = 140f;
        private const float WindowPadding = 8f;

        private sealed class ChannelVisual
        {
            public MonitorCurveDescriptor Descriptor = null!;
            public GameObject Root = null!;
            public TextMeshProUGUI ReadoutText = null!;
            public Texture2D Tex = null!;
            public Color32[] BaseGrid = null!;
            public Color32[] PixelBuffer = null!;
            public int WriteX;
            public int LastY;
            public bool HasLastY;
            public float TimeAccum;
            public float ReadoutTimer;
        }

        private static PhysiologyMonitorOverlay? _instance;

        private RectTransform? _canvasRect;
        private GameObject? _windowGo;
        private RectTransform? _windowRect;
        private MonitorDragHandle? _dragHandle;
        private RectTransform? _toggleBarRect;
        private RectTransform? _channelsContainerRect;
        private GameObject? _emptyHintGo;
        private TMP_FontAsset? _sharedFont;

        private readonly List<ChannelVisual> _channels = new List<ChannelVisual>();
        private Body? _targetBody;
        private bool _isOpen;
        private bool _layoutDirty = true;

        public static bool IsOpen => _instance != null && _instance._isOpen;

        public static void ToggleVisible(Body body)
        {
            if (body == null || !body.alive || WorldGeneration.unchipped)
                return;

            var inst = EnsureInstance();
            if (inst == null) return;

            bool targetEnabled = !inst._isOpen;
            RealisticEcgPlugin.SaveMonitorOverlayEnabled(targetEnabled);

            if (targetEnabled)
            {
                inst.OpenInternal(body, playSound: true);
            }
            else
            {
                inst.HideInternal();
                if (PlayerCamera.main != null)
                    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, 0.95f);
            }
        }

        public static void SetEnabledAndShow(Body body, bool enabled)
        {
            if (body == null || !body.alive || WorldGeneration.unchipped)
                return;

            RealisticEcgPlugin.SaveMonitorOverlayEnabled(enabled);

            if (enabled)
            {
                var inst = EnsureInstance();
                inst?.OpenInternal(body, playSound: true);
            }
            else
            {
                _instance?.HideInternal();
            }
        }

        public static void SyncWithWoundView(WoundView? view)
        {
            Body? targetBody = view != null ? view.body : null;
            bool woundViewOpen = view != null && view.gameObject.activeInHierarchy;
            if (!woundViewOpen || WorldGeneration.unchipped || targetBody == null || !targetBody.alive)
            {
                // Temporarily hide without overwriting saved WindowEnabled preference
                if (_instance != null && _instance._isOpen)
                {
                    _instance.HideInternal();
                }
                return;
            }

            bool savedEnabled = RealisticEcgPlugin.GetSavedMonitorOverlayEnabled();
            if (savedEnabled)
            {
                var inst = EnsureInstance();
                if (inst == null) return;
                inst._targetBody = targetBody;
                if (!inst._isOpen)
                {
                    inst.OpenInternal(targetBody, playSound: false);
                }
            }
            else if (_instance != null)
            {
                _instance._targetBody = targetBody;
                if (_instance._isOpen)
                {
                    _instance.HideInternal();
                }
            }
        }

        public static void DestroyInstance()
        {
            if (_instance != null)
            {
                PhysiologyMonitorApi.OnCurvesChanged -= _instance.MarkLayoutDirty;
                foreach (var ch in _instance._channels)
                {
                    if (ch.Tex != null) Destroy(ch.Tex);
                }
                _instance._channels.Clear();
                if (_instance._windowGo != null) Destroy(_instance._windowGo);
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        private static PhysiologyMonitorOverlay? EnsureInstance()
        {
            if (_instance != null && _instance._windowGo != null)
                return _instance;

            Canvas? targetCanvas = ResolveHostCanvas();
            if (targetCanvas == null)
                return null;

            // Stretch host RectTransform across the entire Canvas (0,0 -> 1,1) so the floating window can drag freely across the full screen
            var hostGo = new GameObject("PhysiologyMonitorOverlayHost", typeof(RectTransform));
            hostGo.transform.SetParent(targetCanvas.transform, false);
            var hostRt = hostGo.GetComponent<RectTransform>();
            hostRt.anchorMin = Vector2.zero;
            hostRt.anchorMax = Vector2.one;
            hostRt.offsetMin = Vector2.zero;
            hostRt.offsetMax = Vector2.zero;
            LayoutRebuilder.ForceRebuildLayoutImmediate(hostRt);

            var host = hostGo.AddComponent<PhysiologyMonitorOverlay>();
            host._canvasRect = hostRt;
            host._sharedFont = ResolveFont();
            host.BuildWindowUi();
            PhysiologyMonitorApi.OnCurvesChanged += host.MarkLayoutDirty;
            _instance = host;
            return host;
        }

        private static Canvas? ResolveHostCanvas()
        {
            if (WoundView.view != null)
            {
                var c = WoundView.view.GetComponentInParent<Canvas>();
                if (c != null) return c.rootCanvas != null ? c.rootCanvas : c;
            }
            if (PlayerCamera.main != null && PlayerCamera.main.woundView != null)
            {
                var c = PlayerCamera.main.woundView.GetComponentInParent<Canvas>();
                if (c != null) return c.rootCanvas != null ? c.rootCanvas : c;
            }
            return FindObjectOfType<Canvas>();
        }

        private static TMP_FontAsset? ResolveFont()
        {
            if (WoundView.view != null && WoundView.view.bpmText != null)
                return WoundView.view.bpmText.font;
            return TMP_Settings.defaultFontAsset;
        }

        private void MarkLayoutDirty()
        {
            _layoutDirty = true;
        }

        private void OpenInternal(Body body, bool playSound)
        {
            _targetBody = body;
            _isOpen = true;
            _layoutDirty = true;
            transform.SetAsLastSibling();
            if (_windowGo != null)
            {
                _windowGo.SetActive(true);
                _windowGo.transform.SetAsLastSibling();
            }
            RebuildChannelsIfNeeded();
            if (_windowRect != null && _dragHandle != null)
            {
                _windowRect.anchoredPosition = _dragHandle.ClampToParent(RealisticEcgPlugin.GetSavedMonitorOverlayPosition());
            }
            ResetAllSweepHeads();
            if (playSound && PlayerCamera.main != null)
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, 1.05f);
        }

        private void HideInternal()
        {
            _isOpen = false;
            if (_windowGo != null)
            {
                _windowGo.SetActive(false);
            }
        }

        private void UserCloseButtonClicked()
        {
            RealisticEcgPlugin.SaveMonitorOverlayEnabled(false);
            HideInternal();
            if (PlayerCamera.main != null)
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, 0.95f);
        }

        private void BuildWindowUi()
        {
            _windowGo = new GameObject("PhysiologyMonitorWindow", typeof(RectTransform), typeof(Image));
            _windowGo.transform.SetParent(transform, false);
            _windowRect = _windowGo.GetComponent<RectTransform>();
            _windowRect.anchorMin = new Vector2(1f, 1f);
            _windowRect.anchorMax = new Vector2(1f, 1f);
            _windowRect.pivot = new Vector2(1f, 1f);
            _windowRect.sizeDelta = new Vector2(WindowWidth, HeaderHeight + ToggleBarHeight + ChannelBlockHeight + WindowPadding * 2f);
            _windowRect.anchoredPosition = RealisticEcgPlugin.GetSavedMonitorOverlayPosition();

            var bg = _windowGo.GetComponent<Image>();
            bg.color = new Color(0.05f, 0.08f, 0.09f, 0.94f);

            var outline = _windowGo.AddComponent<Outline>();
            outline.effectColor = new Color(0.20f, 0.45f, 0.42f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Draggable header bar
            var headerGo = new GameObject("Header", typeof(RectTransform), typeof(Image));
            headerGo.transform.SetParent(_windowGo.transform, false);
            var headerRt = headerGo.GetComponent<RectTransform>();
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.sizeDelta = new Vector2(0f, HeaderHeight);
            headerRt.anchoredPosition = Vector2.zero;
            headerGo.GetComponent<Image>().color = new Color(0.09f, 0.16f, 0.18f, 0.98f);

            _dragHandle = headerGo.AddComponent<MonitorDragHandle>();
            _dragHandle.TargetWindow = _windowRect;
            _dragHandle.BoundsRect = _canvasRect;

            var titleTmp = CreateText(headerGo.transform, "Title", "实时生理监护曲线 (可拖动)", 13f, new Color(0.76f, 0.95f, 0.92f, 1f), TextAlignmentOptions.MidlineLeft);
            var titleRt = titleTmp.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 0f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.offsetMin = new Vector2(10f, 0f);
            titleRt.offsetMax = new Vector2(-34f, 0f);
            titleTmp.raycastTarget = false;

            // Close button [X]
            var closeGo = new GameObject("CloseBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            closeGo.transform.SetParent(headerGo.transform, false);
            var closeRt = closeGo.GetComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(1f, 0.5f);
            closeRt.anchorMax = new Vector2(1f, 0.5f);
            closeRt.pivot = new Vector2(1f, 0.5f);
            closeRt.sizeDelta = new Vector2(22f, 20f);
            closeRt.anchoredPosition = new Vector2(-5f, 0f);
            closeGo.GetComponent<Image>().color = new Color(0.38f, 0.14f, 0.14f, 0.92f);
            var closeTmp = CreateText(closeGo.transform, "X", "×", 15f, new Color(1f, 0.85f, 0.85f, 1f), TextAlignmentOptions.Center);
            StretchFull(closeTmp.rectTransform);
            closeTmp.raycastTarget = false;
            closeGo.GetComponent<Button>().onClick.AddListener(UserCloseButtonClicked);

            // Per-curve visibility toggle bar
            var toggleBarGo = new GameObject("CurveToggleBar", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
            toggleBarGo.transform.SetParent(_windowGo.transform, false);
            _toggleBarRect = toggleBarGo.GetComponent<RectTransform>();
            _toggleBarRect.anchorMin = new Vector2(0f, 1f);
            _toggleBarRect.anchorMax = new Vector2(1f, 1f);
            _toggleBarRect.pivot = new Vector2(0.5f, 1f);
            _toggleBarRect.sizeDelta = new Vector2(-WindowPadding * 2f, ToggleBarHeight);
            _toggleBarRect.anchoredPosition = new Vector2(0f, -(HeaderHeight + 4f));
            toggleBarGo.GetComponent<Image>().color = new Color(0.06f, 0.10f, 0.12f, 0.85f);

            var hlg = toggleBarGo.GetComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(4, 4, 2, 2);
            hlg.spacing = 4f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = false;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;

            // Stacked channels container
            var channelsGo = new GameObject("ChannelsContainer", typeof(RectTransform));
            channelsGo.transform.SetParent(_windowGo.transform, false);
            _channelsContainerRect = channelsGo.GetComponent<RectTransform>();
            _channelsContainerRect.anchorMin = new Vector2(0f, 1f);
            _channelsContainerRect.anchorMax = new Vector2(1f, 1f);
            _channelsContainerRect.pivot = new Vector2(0.5f, 1f);
            _channelsContainerRect.anchoredPosition = new Vector2(0f, -(HeaderHeight + ToggleBarHeight + 8f));
            _channelsContainerRect.sizeDelta = new Vector2(-WindowPadding * 2f, ChannelBlockHeight);

            // Empty hint when all curves are hidden
            _emptyHintGo = new GameObject("EmptyHint", typeof(RectTransform));
            _emptyHintGo.transform.SetParent(_channelsContainerRect, false);
            var hintRt = _emptyHintGo.GetComponent<RectTransform>();
            StretchFull(hintRt);
            var hintTmp = CreateText(_emptyHintGo.transform, "HintText", "所有曲线已隐藏 — 点击上方曲线按钮重新显示", 12.5f, new Color(0.62f, 0.72f, 0.75f, 0.9f), TextAlignmentOptions.Center);
            StretchFull(hintTmp.rectTransform);
            hintTmp.raycastTarget = false;
            _emptyHintGo.SetActive(false);

            _windowGo.SetActive(false);
        }

        private void RebuildChannelsIfNeeded()
        {
            if (!_layoutDirty || _toggleBarRect == null || _channelsContainerRect == null || _windowRect == null)
                return;
            _layoutDirty = false;

            // Clear old toggle buttons
            for (int i = _toggleBarRect.childCount - 1; i >= 0; i--)
                Destroy(_toggleBarRect.GetChild(i).gameObject);

            // Clear old channel visuals & textures
            foreach (var ch in _channels)
            {
                if (ch.Tex != null) Destroy(ch.Tex);
                if (ch.Root != null) Destroy(ch.Root);
            }
            _channels.Clear();

            var ordered = PhysiologyMonitorApi.GetOrderedCurvesSnapshot();
            int visibleCount = 0;

            foreach (var desc in ordered)
            {
                bool visible = PhysiologyMonitorApi.IsCurveVisible(desc.Id);
                CreateCurveToggleButton(desc, visible);

                if (visible)
                {
                    var visual = CreateChannelVisual(desc, visibleCount);
                    _channels.Add(visual);
                    visibleCount++;
                }
            }

            float contentHeight = visibleCount > 0 ? visibleCount * ChannelBlockHeight + (visibleCount - 1) * 6f : 44f;
            _channelsContainerRect.sizeDelta = new Vector2(-WindowPadding * 2f, contentHeight);
            if (_emptyHintGo != null)
                _emptyHintGo.SetActive(visibleCount == 0);

            float totalHeight = HeaderHeight + ToggleBarHeight + 14f + contentHeight + WindowPadding;
            _windowRect.sizeDelta = new Vector2(WindowWidth, totalHeight);
            if (_dragHandle != null)
            {
                _windowRect.anchoredPosition = _dragHandle.ClampToParent(_windowRect.anchoredPosition);
            }
        }

        private void CreateCurveToggleButton(MonitorCurveDescriptor desc, bool isVisible)
        {
            if (_toggleBarRect == null) return;

            var btnGo = new GameObject("Toggle_" + desc.Id, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            btnGo.transform.SetParent(_toggleBarRect, false);

            float estWidth = Mathf.Clamp(desc.DisplayName.Length * 11f + 26f, 76f, 150f);
            var le = btnGo.GetComponent<LayoutElement>();
            le.preferredWidth = estWidth;
            le.minWidth = 64f;

            var img = btnGo.GetComponent<Image>();
            img.color = isVisible
                ? new Color(desc.TraceColor.r * 0.25f + 0.08f, desc.TraceColor.g * 0.25f + 0.08f, desc.TraceColor.b * 0.25f + 0.08f, 0.95f)
                : new Color(0.11f, 0.13f, 0.15f, 0.72f);

            string prefix = isVisible ? "● " : "○ ";
            Color textColor = isVisible
                ? Color.Lerp(desc.TraceColor, Color.white, 0.45f)
                : new Color(0.50f, 0.56f, 0.58f, 0.85f);

            var tmp = CreateText(btnGo.transform, "Text", prefix + desc.DisplayName, 11.5f, textColor, TextAlignmentOptions.Center);
            StretchFull(tmp.rectTransform);
            tmp.raycastTarget = false;

            var tooltip = btnGo.AddComponent<UITooltip>();
            tooltip.skipLocale = true;
            tooltip.tipName = desc.DisplayName;
            tooltip.tipDesc = isVisible ? "点击隐藏此监测曲线" : "点击显示此监测曲线";

            string curveId = desc.Id;
            btnGo.GetComponent<Button>().onClick.AddListener(() =>
            {
                PhysiologyMonitorApi.SetCurveVisible(curveId, !PhysiologyMonitorApi.IsCurveVisible(curveId));
                if (PlayerCamera.main != null)
                    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.MiniClick, 1.0f);
            });
        }

        private ChannelVisual CreateChannelVisual(MonitorCurveDescriptor desc, int index)
        {
            var rootGo = new GameObject("Channel_" + desc.Id, typeof(RectTransform), typeof(Image));
            rootGo.transform.SetParent(_channelsContainerRect, false);

            var rt = rootGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, ChannelBlockHeight);
            rt.anchoredPosition = new Vector2(0f, -(index * (ChannelBlockHeight + 6f)));
            rootGo.GetComponent<Image>().color = new Color(0.03f, 0.06f, 0.07f, 0.96f);

            // Top header line: curve name (left) + live readout (right)
            var nameTmp = CreateText(rootGo.transform, "Name", desc.DisplayName, 12f, Color.Lerp(desc.TraceColor, Color.white, 0.35f), TextAlignmentOptions.MidlineLeft);
            var nameRt = nameTmp.rectTransform;
            nameRt.anchorMin = new Vector2(0f, 1f);
            nameRt.anchorMax = new Vector2(0.55f, 1f);
            nameRt.pivot = new Vector2(0f, 1f);
            nameRt.sizeDelta = new Vector2(-12f, 20f);
            nameRt.anchoredPosition = new Vector2(6f, -2f);
            nameTmp.raycastTarget = false;

            var readoutTmp = CreateText(rootGo.transform, "Readout", "--", 12.5f, Color.Lerp(desc.TraceColor, Color.white, 0.55f), TextAlignmentOptions.MidlineRight);
            var readoutRt = readoutTmp.rectTransform;
            readoutRt.anchorMin = new Vector2(0.45f, 1f);
            readoutRt.anchorMax = new Vector2(1f, 1f);
            readoutRt.pivot = new Vector2(1f, 1f);
            readoutRt.sizeDelta = new Vector2(-12f, 20f);
            readoutRt.anchoredPosition = new Vector2(-6f, -2f);
            readoutTmp.raycastTarget = false;

            // Plot RawImage
            var plotGo = new GameObject("Plot", typeof(RectTransform), typeof(RawImage));
            plotGo.transform.SetParent(rootGo.transform, false);
            var plotRt = plotGo.GetComponent<RectTransform>();
            plotRt.anchorMin = new Vector2(0f, 0f);
            plotRt.anchorMax = new Vector2(1f, 1f);
            plotRt.offsetMin = new Vector2(4f, 4f);
            plotRt.offsetMax = new Vector2(-4f, -22f);

            var tex = new Texture2D(PlotTexWidth, PlotTexHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color32[] baseGrid = BuildChannelBackgroundGrid(desc, PlotTexWidth, PlotTexHeight);
            var buf = new Color32[baseGrid.Length];
            Array.Copy(baseGrid, buf, baseGrid.Length);
            tex.SetPixels32(buf);
            tex.Apply(false);

            var rawImg = plotGo.GetComponent<RawImage>();
            rawImg.texture = tex;
            rawImg.raycastTarget = false;

            // Reference line numeric labels on the right edge of the plot
            AddAxisLabels(plotGo.transform, desc);

            return new ChannelVisual
            {
                Descriptor = desc,
                Root = rootGo,
                ReadoutText = readoutTmp,
                Tex = tex,
                BaseGrid = baseGrid,
                PixelBuffer = buf,
                WriteX = 0,
                LastY = PlotTexHeight / 2,
                HasLastY = false,
                TimeAccum = 0f,
                ReadoutTimer = 0f
            };
        }

        private void AddAxisLabels(Transform plotParent, MonitorCurveDescriptor desc)
        {
            float span = Mathf.Max(0.001f, desc.MaxValue - desc.MinValue);
            var labelsToDraw = new List<float> { desc.MinValue, desc.MaxValue };
            if (desc.ReferenceLines != null)
            {
                foreach (float refVal in desc.ReferenceLines)
                {
                    if (refVal > desc.MinValue && refVal < desc.MaxValue)
                        labelsToDraw.Add(refVal);
                }
            }

            foreach (float val in labelsToDraw)
            {
                float norm = Mathf.Clamp01((val - desc.MinValue) / span);
                var tmp = CreateText(
                    plotParent,
                    "Ref_" + val.ToString("0"),
                    val.ToString("0"),
                    9.5f,
                    new Color(0.62f, 0.76f, 0.78f, 0.72f),
                    TextAlignmentOptions.MidlineRight);
                var rt = tmp.rectTransform;
                rt.anchorMin = new Vector2(1f, norm);
                rt.anchorMax = new Vector2(1f, norm);
                rt.pivot = new Vector2(1f, norm > 0.92f ? 1f : (norm < 0.08f ? 0f : 0.5f));
                rt.sizeDelta = new Vector2(34f, 12f);
                rt.anchoredPosition = new Vector2(-3f, 0f);
                tmp.raycastTarget = false;
            }
        }

        private static Color32[] BuildChannelBackgroundGrid(MonitorCurveDescriptor desc, int w, int h)
        {
            var bg = new Color32(10, 17, 20, 255);
            var border = new Color32(34, 62, 68, 255);
            var refLine = new Color32(46, 86, 94, 255);
            var vertGrid = new Color32(18, 34, 38, 255);

            var grid = new Color32[w * h];
            for (int i = 0; i < grid.Length; i++)
                grid[i] = bg;

            // Vertical 1-second time guides
            int seconds = Mathf.Max(1, Mathf.RoundToInt(desc.SweepSpanSeconds));
            for (int s = 1; s < seconds; s++)
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt(w * (s / (float)seconds)), 1, w - 2);
                for (int y = 1; y < h - 1; y += 2)
                    grid[y * w + gx] = vertGrid;
            }

            // Horizontal reference lines (e.g. 50, 100, 150, 200 mmHg for ABP)
            float span = Mathf.Max(0.001f, desc.MaxValue - desc.MinValue);
            if (desc.ReferenceLines != null)
            {
                foreach (float val in desc.ReferenceLines)
                {
                    if (val <= desc.MinValue || val >= desc.MaxValue) continue;
                    int gy = Mathf.Clamp(Mathf.RoundToInt(((val - desc.MinValue) / span) * (h - 3)) + 1, 1, h - 2);
                    for (int x = 1; x < w - 1; x++)
                    {
                        // Subtle dashed reference line
                        if ((x & 3) != 3)
                            grid[gy * w + x] = refLine;
                    }
                }
            }

            // 1px outer frame
            for (int x = 0; x < w; x++)
            {
                grid[x] = border;
                grid[(h - 1) * w + x] = border;
            }
            for (int y = 0; y < h; y++)
            {
                grid[y * w] = border;
                grid[y * w + (w - 1)] = border;
            }

            return grid;
        }

        private void ResetAllSweepHeads()
        {
            foreach (var ch in _channels)
            {
                Array.Copy(ch.BaseGrid, ch.PixelBuffer, ch.BaseGrid.Length);
                ch.WriteX = 0;
                ch.HasLastY = false;
                ch.TimeAccum = 0f;
                ch.Tex.SetPixels32(ch.PixelBuffer);
                ch.Tex.Apply(false);
            }
        }

        private void Update()
        {
            bool woundOpen = (WoundView.view != null && WoundView.view.gameObject.activeInHierarchy) ||
                             (PlayerCamera.main != null && PlayerCamera.main.woundView != null && PlayerCamera.main.woundView.activeInHierarchy);
            Body? body = (WoundView.view != null && WoundView.view.body != null)
                ? WoundView.view.body
                : (_targetBody != null ? _targetBody : (PlayerCamera.main != null ? PlayerCamera.main.body : null));

            bool canShow = woundOpen && !WorldGeneration.unchipped && body != null && body.alive;

            if (!canShow)
            {
                if (_isOpen)
                    HideInternal();
                return;
            }

            // Re-open automatically on the exact frame WoundView opens if the saved toggle state is enabled
            if (!_isOpen)
            {
                if (RealisticEcgPlugin.GetSavedMonitorOverlayEnabled())
                {
                    OpenInternal(body!, playSound: false);
                }
                else
                {
                    return;
                }
            }

            _targetBody = body;

            RebuildChannelsIfNeeded();

            // Match ECGVisualizer sweep cadence (including high-speed readable mode when Time.timeScale > 6x)
            bool highSpeedMode = Time.timeScale > 6.0f;
            float dt = highSpeedMode
                ? Mathf.Min(Time.unscaledDeltaTime * 3.0f, 0.20f)
                : Time.deltaTime;
            if (dt <= 0f) return;

            foreach (var ch in _channels)
            {
                UpdateChannelTrace(ch, body!, dt, highSpeedMode);
            }
        }

        private static void UpdateChannelTrace(ChannelVisual ch, Body body, float dt, bool highSpeedMode)
        {
            var desc = ch.Descriptor;
            float spanSec = Mathf.Clamp(desc.SweepSpanSeconds, 1.5f, 20f);
            ch.TimeAccum = Mathf.Min(ch.TimeAccum + dt, spanSec);

            float colDt = spanSec / PlotTexWidth;
            bool texDirty = false;

            const int eraserGapCols = 7;
            float valSpan = Mathf.Max(0.001f, desc.MaxValue - desc.MinValue);
            Color32 traceCore = (Color32)desc.TraceColor;
            Color32 traceEdge = new Color32(
                (byte)(traceCore.r * 0.65f),
                (byte)(traceCore.g * 0.65f),
                (byte)(traceCore.b * 0.65f),
                255);
            Color32 headDot = new Color32(255, 255, 255, 255);

            while (ch.TimeAccum >= colDt)
            {
                ch.TimeAccum -= colDt;
                int prevX = ch.WriteX;
                ch.WriteX++;
                if (ch.WriteX >= PlotTexWidth - 1)
                {
                    ch.WriteX = 1;
                    ch.HasLastY = false;
                }

                // Restore previous cursor dot column back to normal trace color if needed
                if (prevX >= 1 && prevX < PlotTexWidth - 1 && ch.HasLastY)
                {
                    ch.PixelBuffer[ch.LastY * PlotTexWidth + prevX] = traceCore;
                }

                // Restore eraser gap ahead of sweep beam back to clean reference grid
                for (int g = 1; g <= eraserGapCols; g++)
                {
                    int clearX = 1 + ((ch.WriteX - 1 + g) % (PlotTexWidth - 2));
                    for (int cy = 1; cy < PlotTexHeight - 1; cy++)
                    {
                        int p = cy * PlotTexWidth + clearX;
                        ch.PixelBuffer[p] = ch.BaseGrid[p];
                    }
                }

                // Restore current column to background grid before plotting new trace segment
                int wx = ch.WriteX;
                for (int cy = 1; cy < PlotTexHeight - 1; cy++)
                {
                    int p = cy * PlotTexWidth + wx;
                    ch.PixelBuffer[p] = ch.BaseGrid[p];
                }

                float sample;
                if (desc.SampleHistory != null)
                {
                    float offset = highSpeedMode ? 0f : ch.TimeAccum;
                    sample = desc.SampleHistory(body, offset, colDt);
                }
                else if (desc.SampleCurrent != null)
                {
                    sample = desc.SampleCurrent(body);
                }
                else
                {
                    sample = desc.MinValue;
                }

                float norm = Mathf.Clamp01((sample - desc.MinValue) / valSpan);
                int y = Mathf.Clamp(Mathf.RoundToInt(norm * (PlotTexHeight - 3)) + 1, 1, PlotTexHeight - 2);
                int yMin = ch.HasLastY ? Mathf.Min(ch.LastY, y) : y;
                int yMax = ch.HasLastY ? Mathf.Max(ch.LastY, y) : y;

                for (int py = yMin; py <= yMax; py++)
                {
                    ch.PixelBuffer[py * PlotTexWidth + wx] = traceCore;
                }
                if (yMin - 1 >= 1)
                    ch.PixelBuffer[(yMin - 1) * PlotTexWidth + wx] = traceEdge;
                if (yMax + 1 < PlotTexHeight - 1)
                    ch.PixelBuffer[(yMax + 1) * PlotTexWidth + wx] = traceEdge;

                // Bright leading dot
                ch.PixelBuffer[y * PlotTexWidth + wx] = headDot;

                ch.LastY = y;
                ch.HasLastY = true;
                texDirty = true;
            }

            if (texDirty)
            {
                ch.Tex.SetPixels32(ch.PixelBuffer);
                ch.Tex.Apply(false);
            }

            // Refresh numeric readout ~10 Hz
            ch.ReadoutTimer += Time.unscaledDeltaTime;
            if (ch.ReadoutTimer >= 0.10f)
            {
                ch.ReadoutTimer = 0f;
                if (desc.FormatReadout != null)
                {
                    ch.ReadoutText.text = desc.FormatReadout(body);
                }
                else if (desc.SampleCurrent != null)
                {
                    float v = desc.SampleCurrent(body);
                    ch.ReadoutText.text = string.IsNullOrEmpty(desc.Unit) ? $"{v:F1}" : $"{v:F1} {desc.Unit}";
                }
            }
        }

        private TextMeshProUGUI CreateText(Transform parent, string name, string text, float fontSize, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (_sharedFont != null) tmp.font = _sharedFont;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = align;
            return tmp;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
