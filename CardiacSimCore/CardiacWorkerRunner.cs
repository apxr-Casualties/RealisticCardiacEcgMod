using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace CardiacSimCore
{
    /// <summary>
    /// Thread-safe telemetry snapshot published by the background cardiac simulation thread
    /// for instant non-blocking consumption by the Unity main thread (even at 20x time scale).
    /// </summary>
    public struct CardiacTelemetrySnapshot
    {
        public float SimulationTimeSec;
        public float HeartRateBpm;
        public float LeftVentricularStrokeVolumeMl;
        public float RightVentricularStrokeVolumeMl;
        public float LeftVentricularCardiacOutputLpm;
        public float RightVentricularCardiacOutputLpm;
        public float LeftVentricularEjectionFraction;
        public float RightVentricularEjectionFraction;
        public float VentricularSynchrony;
        public float AmiodaroneConcentration;
        public float AmiodaronePendingPool;
        public float LidocaineConcentration;
        public float LidocainePendingPool;
        public float MyocardialAtpReserve;
        public RhythmType Rhythm;
        public bool HasRecentPvc;
        public FrontalEcgSample LatestEcg;
        public long TotalStepsProcessed;
        public double LastBatchComputeMs;
    }

    /// <summary>
    /// Runs CardiacGridSimulator on a dedicated background CPU thread (separate core from Unity main thread).
    /// Accumulates deltaTime (including up to 20x acceleration) and processes commands asynchronously.
    /// </summary>
    public sealed class CardiacWorkerRunner : IDisposable
    {
        private readonly CardiacGridSimulator _simulator;
        private readonly Thread _workerThread;
        private readonly AutoResetEvent _wakeSignal = new AutoResetEvent(false);
        private readonly ConcurrentQueue<Action<CardiacGridSimulator>> _commands = new ConcurrentQueue<Action<CardiacGridSimulator>>();

        private readonly object _timeLock = new object();
        private float _pendingDeltaSec;
        private CardiacEnvironmentInput _pendingEnv;
        private bool _hasPendingEnv;

        private readonly object _snapshotLock = new object();
        private CardiacTelemetrySnapshot _snapshot;
        private readonly FrontalEcgSample[] _sharedEcgHistory = new FrontalEcgSample[CardiacGridSimulator.RingCapacity];
        private int _sharedWritePos;

        private volatile bool _running = true;
        private long _totalSteps;
        private double _lastBatchMs;

        /// <summary>Max backlog of game time queued in worker to prevent runaway spiral if paused/stalled.</summary>
        public float MaxBacklogSec { get; set; } = 4.0f;

        public CardiacWorkerRunner(CardiacGridSimulator? simulator = null)
        {
            _simulator = simulator ?? new CardiacGridSimulator();
            _pendingEnv = _simulator.Environment;
            PublishSnapshot();

            _workerThread = new Thread(WorkerLoop)
            {
                Name = "CasualtiesCardiacSimWorker",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _workerThread.Start();
        }

        /// <summary>
        /// Non-blocking call from main thread each frame (passes Time.deltaTime, e.g. 20x scaled).
        /// </summary>
        public void EnqueueDeltaTime(float deltaSeconds)
        {
            if (deltaSeconds <= 0f) return;
            lock (_timeLock)
            {
                _pendingDeltaSec = Math.Min(MaxBacklogSec, _pendingDeltaSec + deltaSeconds);
            }
            _wakeSignal.Set();
        }

        public void UpdateEnvironment(CardiacEnvironmentInput env)
        {
            lock (_timeLock)
            {
                _pendingEnv = env;
                _hasPendingEnv = true;
            }
            _wakeSignal.Set();
        }

        public void EnqueueCommand(Action<CardiacGridSimulator> command)
        {
            if (command == null) return;
            _commands.Enqueue(command);
            _wakeSignal.Set();
        }

        /// <summary>
        /// Immediately resets pending queue, simulator state, and published snapshot to normal sinus rhythm.
        /// Used by Heal / LastStand so the very next frame sees normal sinus rhythm without async lag.
        /// </summary>
        public void ResetImmediateToSinus(bool clearDrugs, float targetBpm = 72f)
        {
            while (_commands.TryDequeue(out _)) { }
            lock (_timeLock)
            {
                _pendingDeltaSec = 0f;
                _hasPendingEnv = false;
            }
            _commands.Enqueue(sim =>
            {
                sim.ResetToSinus(clearDrugs, resetEnvironment: true, targetBpm: targetBpm);
                // Pre-generate 1.2s of clean sinus waveform into ring buffer so ECG screen starts cleanly
                for (int k = 0; k < 1200; k++)
                    sim.StepOneMillisecond();
            });

            lock (_snapshotLock)
            {
                Array.Clear(_sharedEcgHistory, 0, _sharedEcgHistory.Length);
                _sharedWritePos = 0;
                _snapshot.HeartRateBpm = targetBpm;
                _snapshot.LeftVentricularStrokeVolumeMl = 78f;
                _snapshot.RightVentricularStrokeVolumeMl = 78f;
                _snapshot.LeftVentricularCardiacOutputLpm = (78f * targetBpm) / 1000f;
                _snapshot.RightVentricularCardiacOutputLpm = _snapshot.LeftVentricularCardiacOutputLpm;
                _snapshot.LeftVentricularEjectionFraction = 0.60f;
                _snapshot.RightVentricularEjectionFraction = 0.60f;
                _snapshot.VentricularSynchrony = 1f;
                _snapshot.MyocardialAtpReserve = 1f;
                if (clearDrugs)
                {
                    _snapshot.AmiodaroneConcentration = 0f;
                    _snapshot.AmiodaronePendingPool = 0f;
                    _snapshot.LidocaineConcentration = 0f;
                    _snapshot.LidocainePendingPool = 0f;
                }
                _snapshot.HasRecentPvc = false;
                _snapshot.Rhythm = targetBpm > 100f
                    ? RhythmType.SinusTachycardia
                    : (targetBpm < 60f ? RhythmType.SinusBradycardia : RhythmType.NormalSinus);
            }
            _wakeSignal.Set();
        }

        public void RestoreSavedStateImmediate(
            float amioConc,
            float amioPending,
            float lidoConc,
            float lidoPending,
            float atpReserve,
            RhythmType rhythm,
            float targetBpm)
        {
            while (_commands.TryDequeue(out _)) { }
            lock (_timeLock)
            {
                _pendingDeltaSec = 0f;
                _hasPendingEnv = false;
            }
            _commands.Enqueue(sim => sim.RestoreSavedState(amioConc, amioPending, lidoConc, lidoPending, atpReserve, rhythm, targetBpm));
            _wakeSignal.Set();
        }

        /// <summary>
        /// Copy the most recent history chronological samples (oldest -> newest) for multi-lead strip rendering.
        /// Returns actual number of valid samples copied.
        /// </summary>
        public int CopyRecentSamplesChronological(FrontalEcgSample[] destination, int maxCount)
        {
            if (destination == null || maxCount <= 0) return 0;
            lock (_snapshotLock)
            {
                int count = Math.Min(maxCount, Math.Min(destination.Length, CardiacGridSimulator.RingCapacity));
                int start = (_sharedWritePos - count + CardiacGridSimulator.RingCapacity) % CardiacGridSimulator.RingCapacity;
                for (int k = 0; k < count; k++)
                {
                    destination[k] = _sharedEcgHistory[(start + k) % CardiacGridSimulator.RingCapacity];
                }
                return count;
            }
        }

        public CardiacTelemetrySnapshot GetTelemetrySnapshot()
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }

        /// <summary>
        /// Non-blocking peak-preserving ECG lead readout for ECGVisualizer.
        /// </summary>
        public float SampleLeadPeakPreserving(FrontalLead lead, float offsetSec, float windowSec)
        {
            lock (_snapshotLock)
            {
                int centerBack = ClampInt((int)Math.Round(offsetSec * CardiacGridSimulator.SampleRateHz), 0, CardiacGridSimulator.RingCapacity - 1);
                int span = Math.Max(1, (int)Math.Round(windowSec * CardiacGridSimulator.SampleRateHz));
                float best = 0f;
                float bestAbs = -1f;
                for (int k = 0; k < span; k++)
                {
                    int b = Math.Min(CardiacGridSimulator.RingCapacity - 1, centerBack + k);
                    int idx = (_sharedWritePos - 1 - b + CardiacGridSimulator.RingCapacity) % CardiacGridSimulator.RingCapacity;
                    float v = _sharedEcgHistory[idx].GetLead(lead);
                    float av = Math.Abs(v);
                    if (av > bestAbs)
                    {
                        bestAbs = av;
                        best = v;
                    }
                }
                return best;
            }
        }

        /// <summary>
        /// Non-blocking peak-preserving synchronized 6-lead sample at the instant of peak frontal vector magnitude inside the column window.
        /// </summary>
        public FrontalEcgSample SampleSynchronizedPeakPreserving(float offsetSec, float windowSec)
        {
            lock (_snapshotLock)
            {
                int centerBack = ClampInt((int)Math.Round(offsetSec * CardiacGridSimulator.SampleRateHz), 0, CardiacGridSimulator.RingCapacity - 1);
                int span = Math.Max(1, (int)Math.Round(windowSec * CardiacGridSimulator.SampleRateHz));
                FrontalEcgSample best = default;
                float bestMag = -1f;
                for (int k = 0; k < span; k++)
                {
                    int b = Math.Min(CardiacGridSimulator.RingCapacity - 1, centerBack + k);
                    int idx = (_sharedWritePos - 1 - b + CardiacGridSimulator.RingCapacity) % CardiacGridSimulator.RingCapacity;
                    FrontalEcgSample s = _sharedEcgHistory[idx];
                    float mag = Math.Abs(s.I) + Math.Abs(s.II) + Math.Abs(s.aVF);
                    if (mag > bestMag)
                    {
                        bestMag = mag;
                        best = s;
                    }
                }
                return best;
            }
        }

        private void WorkerLoop()
        {
            var sw = new Stopwatch();
            while (_running)
            {
                _wakeSignal.WaitOne(8);
                if (!_running) break;

                while (_commands.TryDequeue(out var cmd))
                {
                    cmd(_simulator);
                }

                float dt = 0f;
                bool applyEnv = false;
                CardiacEnvironmentInput env = default;

                lock (_timeLock)
                {
                    if (_hasPendingEnv)
                    {
                        env = _pendingEnv;
                        applyEnv = true;
                        _hasPendingEnv = false;
                    }
                    if (_pendingDeltaSec > 0f)
                    {
                        dt = _pendingDeltaSec;
                        _pendingDeltaSec = 0f;
                    }
                }

                if (applyEnv)
                {
                    _simulator.Environment = env;
                }

                if (dt > 0f)
                {
                    sw.Restart();
                    int steps = Math.Max(1, (int)Math.Round(dt * 1000f));
                    _simulator.Step(dt);
                    sw.Stop();

                    _totalSteps += steps;
                    _lastBatchMs = sw.Elapsed.TotalMilliseconds;
                    PublishSnapshot();
                }
                else
                {
                    PublishSnapshot();
                }
            }
        }

        private void PublishSnapshot()
        {
            lock (_snapshotLock)
            {
                _snapshot = new CardiacTelemetrySnapshot
                {
                    SimulationTimeSec = _simulator.SimulationTimeSec,
                    HeartRateBpm = _simulator.MeasuredHeartRateBpm,
                    LeftVentricularStrokeVolumeMl = _simulator.GetLeftVentricularEjectionVolumeMl(),
                    RightVentricularStrokeVolumeMl = _simulator.GetRightVentricularEjectionVolumeMl(),
                    LeftVentricularCardiacOutputLpm = _simulator.GetLeftVentricularCardiacOutputLpm(),
                    RightVentricularCardiacOutputLpm = _simulator.GetRightVentricularCardiacOutputLpm(),
                    LeftVentricularEjectionFraction = _simulator.GetLeftVentricularEjectionFraction(),
                    RightVentricularEjectionFraction = _simulator.GetRightVentricularEjectionFraction(),
                    VentricularSynchrony = _simulator.VentricularSynchrony,
                    AmiodaroneConcentration = _simulator.AmiodaroneConcentration,
                    AmiodaronePendingPool = _simulator.AmiodaronePendingPool,
                    LidocaineConcentration = _simulator.LidocaineConcentration,
                    LidocainePendingPool = _simulator.LidocainePendingPool,
                    MyocardialAtpReserve = _simulator.MyocardialAtpReserve,
                    Rhythm = _simulator.CurrentRhythm,
                    HasRecentPvc = (_simulator.SimulationTimeSec - _simulator.LastEctopicBeatTimeSec) <= 4.5f &&
                                   _simulator.CurrentRhythm != RhythmType.VentricularTachycardia &&
                                   _simulator.CurrentRhythm != RhythmType.VentricularFibrillation &&
                                   _simulator.CurrentRhythm != RhythmType.Asystole,
                    LatestEcg = _simulator.LatestSample,
                    TotalStepsProcessed = _totalSteps,
                    LastBatchComputeMs = _lastBatchMs
                };

                _simulator.CopyRingBufferTo(_sharedEcgHistory, out _sharedWritePos);
            }
        }

        public void Dispose()
        {
            _running = false;
            _wakeSignal.Set();
            if (_workerThread.IsAlive)
                _workerThread.Join(500);
            _wakeSignal.Dispose();
        }

        private static int ClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
