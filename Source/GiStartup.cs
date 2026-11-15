using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TraceSix.Source
{
    // One report per raid start, with Debug Log on: how long each set-up step took on the main thread, a timeline (set-up,
    // voxel volumes built, light volumes filled, the game's raid start, camera jumps, the light on), and the longest
    // frames from the set-up until the light has been on for a few seconds - each with what TraceSix did in that
    // frame. With TraceSix switched off for the raid only the frames are watched: the baseline for a hitch.
    internal static class GiStartup
    {
        private static bool _on, _watchOnly, _voxelsSeen, _cellsSeen, _camKnown;
        private static float _beginReal, _beginUnscaled, _until, _typical, _lightAt;
        private static Vector3 _camAt;
        private static int _jumps;
        private static string _now = "", _prev = "";
        private struct Spike { public float ms, endedAt; public string what; }
        private static readonly List<Spike> _spikes = new List<Spike>();
        private struct Mark { public float at; public string what; }
        private static readonly List<Mark> _marks = new List<Mark>();
        private static readonly StringBuilder _steps = new StringBuilder();
        private static readonly float[] _recent = new float[90];   // the frames before the set-up: what is typical
        private static int _recentN;
        private const float JumpMetres = 25f;      // a camera move this far in one frame is a teleport (spawn, camera swap)
        private const int MostJumps = 6;
        private const float WatchAfterStart = 12f; // watch-only: seconds after the game's raid start

        // watchOnly: TraceSix is off for this raid (nothing set up) - only the frames are watched.
        public static void Begin(bool watchOnly = false)
        {
            _on = true;
            _watchOnly = watchOnly;
            _beginReal = Time.realtimeSinceStartup;
            _beginUnscaled = Time.unscaledTime;
            _until = float.MaxValue;
            _lightAt = -1f;
            _voxelsSeen = _cellsSeen = false;
            _jumps = 0;
            _spikes.Clear();
            _steps.Clear();
            _marks.Clear();
            _now = _prev = "";
            Camera cam = Camera.main;
            _camKnown = cam != null;
            if (_camKnown) _camAt = cam.transform.position;
            AddMark((watchOnly ? "frames watched from here (TraceSix is off)" : "set-up began") + (_camKnown ? ", camera '" + cam.name + "' at " + Place(_camAt) : ", no camera"));
            var sorted = new List<float>();
            for (int i = 0; i < Mathf.Min(_recentN, _recent.Length); i++) sorted.Add(_recent[i]);
            sorted.Sort();
            _typical = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0f;
        }

        // Something TraceSix did this frame.
        public static void Note(string what)
        {
            if (!_on || _now.Contains(what)) return;
            _now = _now.Length == 0 ? what : _now + ", " + what;
        }

        // A set-up step and its main-thread time.
        public static void Step(string what, System.Diagnostics.Stopwatch sw)
        {
            if (!_on) return;
            _steps.Append(_steps.Length > 0 ? ", " : "").Append(what).Append(' ').Append(sw.Elapsed.TotalMilliseconds.ToString("F0")).Append(" ms (done ")
              .Append(When(Time.realtimeSinceStartup)).Append(')');
            Note(what);
            sw.Restart();
        }

        // The main camera's light pass went on: the report ends a few seconds later.
        public static void LightOn()
        {
            if (!_on || _until != float.MaxValue) return;
            _until = Time.unscaledTime + 6f;
            _lightAt = Time.realtimeSinceStartup;
            AddMark("the light switched on (the fade-in starts)");
            Note("the light switched on");
        }

        // First thing in GiRaid.Update: Unity's frame time now is the frame before this one, which goes with
        // what was noted during it.
        public static void Frame()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            if (!_on)
            {
                _recent[_recentN++ % _recent.Length] = ms;
                return;
            }
            float now = Time.realtimeSinceStartup;
            _spikes.Add(new Spike { ms = ms, endedAt = now, what = _prev.Length > 0 ? _prev : _watchOnly ? "(TraceSix off)" : "light updates only" });
            if (_spikes.Count > 64)
            {
                _spikes.Sort((a, b) => b.ms.CompareTo(a.ms));
                _spikes.RemoveRange(16, _spikes.Count - 16);
            }
            _prev = _now;
            _now = "";
            if (!_watchOnly)
            {
                if (!_voxelsSeen && VoxelScene.AllReady) { _voxelsSeen = true; AddMark("the voxel volumes all built (the light volumes start filling)"); }
                if (!_cellsSeen && GiProbes.Ready) { _cellsSeen = true; AddMark("the light volumes filled (each swept twice)"); }
            }
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 p = cam.transform.position;
                if (_camKnown && _jumps < MostJumps && (p - _camAt).sqrMagnitude > JumpMetres * JumpMetres)
                {
                    _jumps++;
                    AddMark("the camera ('" + cam.name + "') moved " + (p - _camAt).magnitude.ToString("F0") + " m in one frame, to " + Place(p));
                }
                _camAt = p;
                _camKnown = true;
            }
            bool done = _watchOnly ? HookValid && now > GiRaid.RaidStartHookTime + WatchAfterStart : Time.unscaledTime > _until;
            if (done || Time.unscaledTime - _beginUnscaled > 120f) Report();
        }

        private static void AddMark(string what) => _marks.Add(new Mark { at = Time.realtimeSinceStartup, what = what });

        private static string Place(Vector3 p) => "(" + p.x.ToString("F0") + ", " + p.y.ToString("F0") + ", " + p.z.ToString("F0") + ")";

        // The hook (OnGameStarted) of this raid, not one left from the last.
        private static bool HookValid => GiRaid.RaidStartHookTime >= _beginReal - 30f;

        // Seconds after the game's raid-start hook (OnGameStarted, the raid's first moment); game time if it has not fired.
        private static string When(float realtime)
        {
            return HookValid ? (realtime - GiRaid.RaidStartHookTime).ToString("+0.0;-0.0") + " s" : "game time " + realtime.ToString("F1") + " s";
        }

        private static void Report()
        {
            _on = false;
            if (HookValid) _marks.Add(new Mark { at = GiRaid.RaidStartHookTime, what = "the game's raid start (OnGameStarted)" });
            _marks.Sort((a, b) => a.at.CompareTo(b.at));
            var sb = new StringBuilder("[TraceSix] Start-up");
            sb.Append(_watchOnly ? ", TRACESIX OFF for this raid (frames only: the baseline)" : "")
              .Append(" (times = seconds after the game's raid start): typical frame before it ").Append(_typical.ToString("F1")).Append(" ms.");
            if (!_watchOnly) sb.Append(" Steps (main thread): ").Append(_steps.Length > 0 ? _steps.ToString() : "none timed").Append('.');
            sb.Append(" Timeline:");
            foreach (Mark m in _marks)
                sb.Append("\n    ").Append(When(m.at).PadLeft(8)).Append("  ").Append(m.what);
            if (!_watchOnly && _lightAt < 0f) sb.Append("\n    (the light did not come on in 120 s)");
            // the longest, in the order they came
            _spikes.Sort((a, b) => b.ms.CompareTo(a.ms));
            if (_spikes.Count > 10) _spikes.RemoveRange(10, _spikes.Count - 10);
            _spikes.Sort((a, b) => a.endedAt.CompareTo(b.endedAt));
            sb.Append("\n  The 10 longest frames from there until ").Append(_watchOnly ? WatchAfterStart.ToString("F0") + " s after the raid start" : "the light had been on 6 s").Append(':');
            foreach (Spike s in _spikes)
                sb.Append("\n    ").Append(s.ms.ToString("F0").PadLeft(5)).Append(" ms, ended ").Append(When(s.endedAt).PadLeft(8)).Append(" - ").Append(s.what);
            Plugin.LogDetail(sb.ToString());
            _spikes.Clear();
            _marks.Clear();
        }
    }
}
