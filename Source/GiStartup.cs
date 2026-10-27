using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TraceSix.Source
{
    // One report per raid start, in the log: how long each set-up step took on the main thread, and the longest
    // frames from the set-up until the light has been on for a few seconds - each with what TraceSix did in that
    // frame. Always on, one line a raid; it is how a start-up hitch is found without guessing.
    internal static class GiStartup
    {
        private static bool _on;
        private static float _start, _until, _typical;
        private static string _now = "", _prev = "";
        private static readonly List<KeyValuePair<float, string>> _spikes = new List<KeyValuePair<float, string>>();
        private static readonly StringBuilder _steps = new StringBuilder();
        private static readonly float[] _recent = new float[90];   // the frames before the set-up: what is typical
        private static int _recentN;

        public static void Begin()
        {
            _on = true;
            _start = Time.unscaledTime;
            _until = float.MaxValue;
            _spikes.Clear();
            _steps.Clear();
            _now = _prev = "";
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
            _steps.Append(_steps.Length > 0 ? ", " : "").Append(what).Append(' ').Append(sw.Elapsed.TotalMilliseconds.ToString("F0")).Append(" ms");
            Note(what);
            sw.Restart();
        }

        // The main camera's light pass went on: the report ends a few seconds later.
        public static void LightOn()
        {
            if (!_on || _until != float.MaxValue) return;
            _until = Time.unscaledTime + 6f;
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
            _spikes.Add(new KeyValuePair<float, string>(ms, _prev.Length > 0 ? _prev : "light updates only"));
            if (_spikes.Count > 64)
            {
                _spikes.Sort((a, b) => b.Key.CompareTo(a.Key));
                _spikes.RemoveRange(16, _spikes.Count - 16);
            }
            _prev = _now;
            _now = "";
            if (Time.unscaledTime > _until || Time.unscaledTime - _start > 120f) Report();
        }

        private static void Report()
        {
            _on = false;
            _spikes.Sort((a, b) => b.Key.CompareTo(a.Key));
            var sb = new StringBuilder("[TraceSix] Start-up: typical frame before it ");
            sb.Append(_typical.ToString("F1")).Append(" ms. Set-up steps (main thread): ").Append(_steps.Length > 0 ? _steps.ToString() : "none timed")
              .Append(". The longest frames from the set-up until the light had been on ").Append(_until != float.MaxValue ? "6 s" : "(it did not come on in 120 s)").Append(':');
            for (int i = 0; i < Mathf.Min(8, _spikes.Count); i++)
                sb.Append("\n    ").Append(_spikes[i].Key.ToString("F0").PadLeft(5)).Append(" ms - ").Append(_spikes[i].Value);
            Plugin.MyLog.LogInfo(sb.ToString());
            _spikes.Clear();
        }
    }
}
