using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TraceSix.Source
{
    // The light volumes (TraceSixProbes.shader), after Yudintsev, "Scalable Real-time Global Illumination for Large
    // Scenes" (GDC 2019): nested volumes of NX x 32 x NZ cells around the player, cells of 0.5 m, 1 m (optional),
    // 2 m and 8 m. Every cell not inside geometry holds the indirect light a surface there would receive facing
    // each of the six axis directions.
    //
    // Each volume is one texture addressed as a torus, so its window follows the camera every frame (Track).
    // A sweep redoes the cells: one slab (Slab z layers) of one volume per frame, 32 rays a cell; the rate is
    // the setting "Light Update Rate".
    internal static class GiProbes
    {
        // Cells per axis (the shader's GN): a setting, "Detail Reach" - 64 or 96 across, 32 high.
        private const int NY = 32;
        private static int NX = 96, NZ = 96;
        private static float Fade = 1f / 16f;             // 1 / the cells a volume's edge fades over (8 at 64 cells, 16 at 96)
        private static readonly int IdGrid = Shader.PropertyToID("_GiGrid");
        // How far the camera may move before the fine VOXEL volume is rebuilt around it, as a share of its width:
        // 0.5 m cells are of use only where the voxels are 0.5 m too, so a 24 m reach needs that volume (64 m
        // across) kept within 6.4 m of the camera, not 12.8.
        public static float FineSlack => NX > 64 || GiConfig.Between.Value ? 0.1f : 0.2f;   // (the 1 m cells, full to 24 m, need it as well)
        // The same for the 1 m voxel volume (128 m across): the 1 m cells are out by 46 m at 96 cells (kept within
        // 12.8 m of the camera it holds 51 m), by 30 m at 64 (38 m held at a fifth).
        public static float MidSlack => NX > 64 ? 0.1f : 0.2f;

        // The volumes' size. Another size means other textures: everything they held is gone and they fill again.
        public static void SetGrid(int cells)
        {
            if (cells == NX || (cells != 64 && cells != 96)) return;
            NX = NZ = cells;
            Fade = cells > 64 ? 1f / 16f : 1f / 8f;
            if (_amb != null) { _amb.Release(); Object.Destroy(_amb); _amb = null; }
            if (_ambRead != null) { _ambRead.Release(); Object.Destroy(_ambRead); _ambRead = null; }
            if (_aim != null) { _aim.Release(); Object.Destroy(_aim); _aim = null; }
            _aimTried = false;
            DropBlend();
            foreach (Grid g in _g)
            {
                g.amb = null;
                g.layer = 0;
                g.sweeps = 0;
            }
            if (_ray != null) { _ray.Release(); Object.Destroy(_ray); _ray = null; }
            if (_state != null) { _state.Release(); Object.Destroy(_state); _state = null; }
            DropScratch();
            _fresh = true;
        }

        private static void DropScratch()
        {
            if (_scratch != null) { _scratch.Release(); Object.Destroy(_scratch); _scratch = null; }
            if (_aimScratch != null) { _aimScratch.Release(); Object.Destroy(_aimScratch); _aimScratch = null; }
            if (_refillRT != null) { _refillRT.Release(); Object.Destroy(_refillRT); _refillRT = null; }
            for (int f = 0; f < 6; f++)
                if (_lit[f] != null) { _lit[f].Release(); Object.Destroy(_lit[f]); _lit[f] = null; }
            if (_aimFlat != null) { _aimFlat.Release(); Object.Destroy(_aimFlat); _aimFlat = null; }
            if (_putRT != null) { _putRT.Release(); Object.Destroy(_putRT); _putRT = null; }
            if (_pick != null) { Object.Destroy(_pick); _pick = null; }
            if (_colTex != null) { Object.Destroy(_colTex); _colTex = null; }
            _scatterTried = _scatterOk = _scatterNow = false;
            foreach (Queue<int> q in _newCells) q.Clear();
            System.Array.Clear(_rushLeft, 0, Levels);
        }

        // The cells of a redo, listed (the shader's _GiPick): of every column of a volume one cell, at a height
        // of its own that moves on in a scrambled order, and before those the cells that have just come into
        // reach (Track). To 0.24.42 a redo was one z layer after another: a front travelling through the world,
        // which cells that had just come into reach had to wait for. The six facings are folded in one pass
        // into _lit and written to their places from there (CELLPUT), as are the lobes and the blend - in place
        // of six draws and six copies a layer. A GPU that cannot write to the lobes' format: the layers, as before.
        private const int Stride = 37;                   // coprime to 64 and 96: every height of a column once a sweep
        private static Texture2D _pick, _colTex;         // the list; each column's height in the order (sent once)
        private static byte[] _pickBytes, _colOff;
        private static readonly RenderTexture[] _lit = new RenderTexture[6];
        private static RenderTargetIdentifier[] _litIds;
        private static RenderTexture _aimFlat, _putRT;
        private static int _pLightAll = -1, _pPut = -1, _pBlendPut = -1, _pickTurn;
        private static bool _scatterTried, _scatterOk;
        public static bool ScatterOk => _scatterOk;          // the scattered update runs on this GPU / bundle (the cost log)
        private static bool _scatterNow;                 // this update goes by the list
        private static readonly Queue<int>[] _newCells = { new Queue<int>(), new Queue<int>(), new Queue<int>(), new Queue<int>() };
        private static readonly bool AimUavOk = SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGB64);
        private static readonly int IdPick = Shader.PropertyToID("_GiPick"), IdPickOn = Shader.PropertyToID("_GiPickOn"), IdColOff = Shader.PropertyToID("_GiColOff"),
            IdPutAim = Shader.PropertyToID("_GiPutAim"), IdAimNew = Shader.PropertyToID("_GiAimNew"), IdNoHit = Shader.PropertyToID("_GiNoHit"), IdHitNearest = Shader.PropertyToID("_GiHitNearest");
        private static readonly int[] IdLit =
        {
            Shader.PropertyToID("_GiLit0"), Shader.PropertyToID("_GiLit1"), Shader.PropertyToID("_GiLit2"),
            Shader.PropertyToID("_GiLit3"), Shader.PropertyToID("_GiLit4"), Shader.PropertyToID("_GiLit5")
        };

        // This redo's cells of volume c into _pick.
        private static void FillPick(int c, Grid g)
        {
            int w = NX * Slab;
            for (int s = 0; s < Slab; s++)
            {
                int kz = (g.layer + s) * Stride % NZ;
                for (int y = 0; y < NY; y++)
                {
                    int i = (y * w + s * NX) * 4, col = y * NX;
                    for (int x = 0; x < NX; x++, i += 4)
                    {
                        int z = kz + _colOff[col + x];
                        if (z >= NZ) z -= NZ;
                        _pickBytes[i] = (byte)x;
                        _pickBytes[i + 1] = (byte)y;
                        _pickBytes[i + 2] = (byte)z;
                    }
                }
            }
            // The cells that have just come into reach take the place of up to half of those - from a place in
            // the list that moves on every redo, so no column goes without for long.
            Queue<int> q = _newCells[c];
            int n = w * NY, take = Mathf.Min(q.Count, _rushFull ? n : n / 2), at = (int)((uint)(_pickTurn++ * 977) % (uint)n);
            for (int j = 0; j < take; j++)
            {
                int v = q.Dequeue(), i = (at + j) % n * 4;
                _pickBytes[i] = (byte)v;
                _pickBytes[i + 1] = (byte)(v >> 8);
                _pickBytes[i + 2] = (byte)(v >> 16);
            }
            _pick.SetPixelData(_pickBytes, 0);
            _pick.Apply(false, false);
        }

        // Volume l's window has moved from old to now: the cells it holds now and did not before, to be redone first.
        private static void EnqueueNew(int l, Vector3Int old, Vector3Int now)
        {
            Queue<int> q = _newCells[l];
            Vector3Int d = now - old;
            // all of it is new (a jump, a new raid), or more is waiting than a few redos can take: the sweep does it
            if (Mathf.Abs(d.x) >= NX || Mathf.Abs(d.y) >= NY || Mathf.Abs(d.z) >= NZ || q.Count > 8 * NX * NY) { q.Clear(); return; }
            for (int a = 0; a < 3; a++)
            {
                int da = d[a];
                if (da == 0) continue;
                int n = a == 0 ? NX : (a == 1 ? NY : NZ);
                // along this axis the layers that came in; along the others the whole window
                int lo = da > 0 ? old[a] + n : now[a], hi = da > 0 ? now[a] + n : old[a];
                for (int u = lo; u < hi; u++)
                    for (int v = 0; v < (a == 0 ? NY : NX); v++)
                        for (int w = 0; w < (a == 2 ? NY : NZ); w++)
                        {
                            // (u, v, w) -> the cell: u along axis a, v and w along the other two in x, y, z order
                            int cx = a == 0 ? u : now.x + v;
                            int cy = a == 1 ? u : now.y + (a == 0 ? v : w);
                            int cz = a == 2 ? u : now.z + w;
                            q.Enqueue(((cx % NX + NX) % NX) | (((cy % NY + NY) % NY) << 8) | (((cz % NZ + NZ) % NZ) << 16));
                        }
            }
        }

        private const int RayW = 8, RayH = 4;             // a cell's 32 rays as a tile of the ray target
        private static int Slab = 4;                      // z layers per update: GiConfig.Layers, a divisor of NZ
        private const float Store = 256f;                 // the shader's STORE
        private const int PassState = 0, PassTrace = 1, PassLight = 2;
        // Share of a cell's old value kept per update. 32 rays per update is a noisy estimate and neighbouring
        // cells are half a metre apart, so the noise would show as blotches; this averages roughly the last
        // twenty updates. The light it tracks changes over minutes. A cell younger than twenty updates holds
        // their plain mean instead (the shader's LIGHT pass), or its first rough guess would stay for seconds.
        private const float History = 0.95f;
        // Four volumes; the second (1 m cells) is the in-between one and can be left out ("In-Between Detail").
        private const int Levels = 4;
        private static readonly float[] CellSize = { 0.5f, 1f, 2f, 8f };
        // Which volume is redone in a frame. One slab of one volume per frame either way, so a fourth volume
        // costs no frame time: the two coarse ones give up half their turns to it.
        private static readonly int[] OrderWith = { 0, 1, 0, 2, 0, 1, 0, 3 };
        private static readonly int[] OrderWithout = { 0, 2, 0, 3 };
        private static bool _between;                     // the 1 m volume is being kept
        private static int _fillTurn;
        private static bool BetweenInUse => _between && _g[1].sweeps >= 2;
        // A door that has just moved (or a lamp switched on or off), and until which redo of the 0.5 m volume the
        // cells around it stay woken. The shader knows one at a time: the others wait their turn (_wakeNext).
        private static Vector4 _wake;
        private static int _wakeUntil;
        private static readonly List<Vector4> _wakeNext = new List<Vector4>();
        // (1.0.3) a lamp's wake (switched, shot out) and not a door's: the shader starts settled cells over (_GiWakeLamp)
        private static bool _wakeLamp;
        private static readonly List<bool> _wakeNextLamp = new List<bool>();
        private static float _lampResetUntil;
        private const float LampResetWindow = 1f;         // s after a switch in which every woken cell's redo replaces what it held
        private static readonly int IdWakeLamp = Shader.PropertyToID("_GiWakeLamp");
        private const int WakeQueue = 16;                 // more waiting (the power switched on): those fade at the usual pace
        private const float MaxWake = 30f;                // two wakes run as one if one sphere this wide holds both
        private static readonly int IdLevels = Shader.PropertyToID("_GiLevels"), IdRest = Shader.PropertyToID("_GiRest"), IdWake = Shader.PropertyToID("_GiWake");

        // The voxels now hold a door where it has moved to: the light cells within 12 m of it find their light
        // anew (the shader's STATE and LIGHT), for five redos of the 0.5 m volume. At once: a lamp's wake that was
        // running waits for its turn again.
        public static void Wake(Vector3 at)
        {
            var w = new Vector4(at.x, at.y, at.z, 12f);
            bool lamp = false;
            if (_wake.w > 0f)
            {
                if (Join(ref w, _wake)) lamp = _wakeLamp;   // one sphere round both: still a lamp's if it was
                else { _wakeNext.Insert(0, _wake); _wakeNextLamp.Insert(0, _wakeLamp); }
            }
            StartWake(w, lamp);
        }

        // (1.0.0) A still lamp switched on or off for good - shot out, a light switch, the power (GiLamps): the cells
        // round it, as round a door, its nearest first. A settled cell keeps 95% of what it held each redo and is not
        // redone every sweep: a shot-out lamp's bounce stayed for minutes, until a door nearby was opened.
        //   How far: not just its reach - every cell that sees the floor and walls it lit holds its bounce, the whole
        // room (his test of reach + 2 m: "still slow, a door nearby darkens it more"). From a pool of light the
        // reach wide, what a cell gets falls off about as (reach / distance)^2: three reaches out about a tenth of
        // what it is beside the pool; past that the cells catch up at the usual pace. At least a door's 12 m.
        // Returns what was done, for the log.
        public static string WakeLamp(Vector3 at, float reach)
        {
            float r = Mathf.Clamp(3f * reach, 12f, MaxWake);
            var w = new Vector4(at.x, at.y, at.z, r);
            // none of the cells reaches it (the inventory's character lights 1 km under the map, his log): nothing to do
            Vector3 lo = (Vector3)_g[Levels - 1].min * CellSize[Levels - 1], hi = lo + new Vector3(NX, NY, NZ) * CellSize[Levels - 1];
            Vector3 near = Vector3.Max(lo, Vector3.Min(hi, at));
            if ((near - at).sqrMagnitude > r * r) return null;
            string within = "the light cells within " + r.ToString("F0") + " m relearn it";
            float rush = LampRush(reach);
            string first = ", those within " + rush.ToString("F0") + " m first";
            if (_wake.w <= 0f) { StartWake(w, true); RushLamp(at, rush); return within + first + " (started)"; }
            if (Join(ref _wake, w))
            {
                _wakeUntil = _g[0].sweeps + 5;
                _wakeLamp = true;
                _lampResetUntil = Time.unscaledTime + LampResetWindow;
                RushLamp(at, rush);
                return within + first + " (joined the running one, now " + _wake.w.ToString("F0") + " m)";
            }
            for (int i = 0; i < _wakeNext.Count; i++)
            {
                Vector4 q = _wakeNext[i];
                if (Join(ref q, w)) { _wakeNext[i] = q; _wakeNextLamp[i] = true; return within + " (joined a waiting one)"; }
            }
            if (_wakeNext.Count >= WakeQueue) return "too many waiting: it fades at the usual pace";
            _wakeNext.Add(w);
            _wakeNextLamp.Add(true);
            return within + " (waits its turn, " + _wakeNext.Count + " waiting)";
        }

        // (1.0.3) How far round a switched lamp the cells are redone first: where its light lands, about twice its reach.
        private static float LampRush(float reach) => Mathf.Clamp(2f * reach, RushRadius, LampRushMax);

        private static void StartWake(Vector4 w, bool lamp)
        {
            _wake = w;
            _wakeUntil = _g[0].sweeps + 5;
            _wakeLamp = lamp;
            if (lamp) _lampResetUntil = Time.unscaledTime + LampResetWindow;
        }

        // a = the smallest sphere round both, if it is no wider than MaxWake
        private static bool Join(ref Vector4 a, Vector4 b)
        {
            float d = Vector3.Distance(a, b);
            if (d + b.w <= a.w) return true;
            if (d + a.w <= b.w) { a = b; return true; }
            float r = (d + a.w + b.w) * 0.5f;
            if (r > MaxWake) return false;
            Vector3 c = (Vector3)a + ((Vector3)b - (Vector3)a) * ((r - a.w) / d);
            a = new Vector4(c.x, c.y, c.z, r);
            return true;
        }

        // ... and the cells within RushRadius of it are redone first, RushRounds times over, nearest first: on
        // the list the cells that have just come into reach go on (FillPick), one round at a time so no redo
        // holds a cell twice. A woken cell takes a third of each redo's light (the shader's LIGHT), so six
        // rounds take it ~90% of the way; the rest of the 12 m follows at the pace of the sweeps.
        private const float RushRadius = 4f;
        private const int RushRounds = 6;
        // (1.0.3, lamps - his "switched off / shot out, not updating as quick as my flashlight", voxel GI): a lamp's
        // woken cells start over (the shader replaces a settled cell's light at its next redo instead of keeping two
        // thirds), so two rounds are enough - over the whole pool of the lamp's light, not 4 m of it - and a lamp's
        // rush may fill a whole redo's list (a door's half of it). Kept to LampRushMax: about 17 000 0.5 m cells a
        // round at 10 m, a few frames of redos.
        private const float LampRushMax = 10f;
        private const int LampRushRounds = 2;
        private static Vector3 _rushAt;
        private static float _rushRadius = RushRadius;
        private static bool _rushFull;                    // the rush may take every cell of a redo's list
        private static readonly int[] _rushLeft = new int[Levels];
        private static readonly Vector3Int[][] _rushOffs = new Vector3Int[Levels][];
        private static readonly float[] _rushOffsR = new float[Levels];
        public static void Rush(Vector3 at)
        {
            _rushAt = at;
            _rushRadius = RushRadius;
            _rushFull = false;
            _rushLeft[0] = RushRounds;
            _rushLeft[1] = _between ? RushRounds : 0;
        }

        private static void RushLamp(Vector3 at, float radius)
        {
            _rushAt = at;
            _rushRadius = radius;
            _rushFull = true;
            _rushLeft[0] = LampRushRounds;
            _rushLeft[1] = _between ? LampRushRounds : 0;
        }

        // The ray budget ("Light Ray Budget"; the shader's STATE): per volume, the share of the cells that would
        // send out rays that is held back this redo. One redo in four of each volume the slab's states come back
        // from the GPU a frame or two later (8 KB at rate 1): how many cells shot whatever the budget (just come
        // into reach, around a door), how many shot that could have been held, how many were. From those the share
        // for its next redos, eased (daGI2: a fixed number of probes a frame, `mul *= lerp(1, target / selected,
        // 0.07)`). A redo's cells are from all over the volume (the scattered order), so one speaks for the whole.
        private const float MaxHold = 0.85f;              // at least this much less than all of the rest still shoots
        private static readonly float[] _hold = new float[Levels];
        private static readonly int[] _wantPct = new int[Levels], _freshPct = new int[Levels], _readTurn = new int[Levels];
        private static readonly System.Action<AsyncGPUReadbackRequest>[] _onState = new System.Action<AsyncGPUReadbackRequest>[Levels];
        private static readonly int IdRayHold = Shader.PropertyToID("_GiRayHold");
        private static readonly bool CanRead = SystemInfo.supportsAsyncGPUReadback;
        private static bool BudgetOn => GiConfig.RayBudget.Value < 100 && CanRead;

        private static void ReadStates(int c)
        {
            if (!CanRead || (_readTurn[c]++ & 3) != 0) return;
            if (_onState[c] == null) { int l = c; _onState[c] = r => OnStates(r, l); }
            _cb.RequestAsyncReadback(_state, _onState[c]);
        }

        private static void OnStates(AsyncGPUReadbackRequest r, int c)
        {
            if (r.hasError) return;
            var px = r.GetData<Color32>();
            int n = px.Length, fresh = 0, free = 0, held = 0;
            if (n == 0) return;
            for (int i = 0; i < n; i++)
            {
                Color32 v = px[i];
                if (v.r >= 191) { if (v.a >= 128) fresh++; else free++; }
                else if (v.b >= 128) held++;
            }
            int want = free + held;
            _wantPct[c] = (fresh + want) * 100 / n;
            _freshPct[c] = fresh * 100 / n;
            float target = GiConfig.RayBudget.Value / 100f * n;
            float hold = want > 0 ? Mathf.Clamp(1f - (target - fresh) / want, 0f, MaxHold) : 0f;
            _hold[c] = Mathf.Lerp(_hold[c], hold, 0.3f);
        }

        // For the cost log: what each volume's redos want and what the budget holds back.
        public static string BudgetSummary()
        {
            if (!CanRead) return "the ray budget cannot work here (no GPU read-back)";
            var sb = new System.Text.StringBuilder("cells that would send rays, of a redo's (just come into reach / by a door, of those): ");
            for (int l = 0; l < Levels; l++)
            {
                if (l == 1 && !_between) continue;
                sb.Append(CellSize[l]).Append(" m ").Append(_wantPct[l]).Append("% (").Append(_freshPct[l]).Append("%)");
                sb.Append(l < Levels - 1 ? ", " : "");
            }
            sb.Append("; held back by the budget (").Append(GiConfig.RayBudget.Value).Append(BudgetOn ? "" : ", NOT in use").Append("): ");
            for (int l = 0; l < Levels; l++)
            {
                if (l == 1 && !_between) continue;
                sb.Append(CellSize[l]).Append(" m ").Append((_hold[l] * 100f).ToString("F0")).Append('%').Append(l < Levels - 1 ? ", " : "");
            }
            return sb.ToString();
        }

        private static void EnqueueRush(int l)
        {
            float S = CellSize[l];
            if (_rushOffs[l] == null || _rushOffsR[l] != _rushRadius)
            {
                _rushOffsR[l] = _rushRadius;
                int r = Mathf.CeilToInt(_rushRadius / S);
                var offs = new List<Vector3Int>();
                for (int z = -r; z <= r; z++)
                    for (int y = -r; y <= r; y++)
                        for (int x = -r; x <= r; x++)
                            if ((x * x + y * y + z * z) * S * S <= _rushRadius * _rushRadius) offs.Add(new Vector3Int(x, y, z));
                offs.Sort((a, b) => a.sqrMagnitude.CompareTo(b.sqrMagnitude));
                _rushOffs[l] = offs.ToArray();
            }
            Grid g = _g[l];
            Queue<int> q = _newCells[l];
            var at = new Vector3Int(Mathf.FloorToInt(_rushAt.x / S), Mathf.FloorToInt(_rushAt.y / S), Mathf.FloorToInt(_rushAt.z / S));
            foreach (Vector3Int o in _rushOffs[l])
            {
                Vector3Int cell = at + o, rel = cell - g.min;
                if (rel.x < 0 || rel.y < 0 || rel.z < 0 || rel.x >= NX || rel.y >= NY || rel.z >= NZ) continue;
                q.Enqueue(((cell.x % NX + NX) % NX) | (((cell.y % NY + NY) % NY) << 8) | (((cell.z % NZ + NZ) % NZ) << 16));
            }
        }
        // The slab a redo works on, before it is copied into the volume (LIGHT cannot render into the texture it
        // reads its history from): Slab z layers x 6 facings.
        private static RenderTexture _scratch, _refillRT;
        private static int _pRefill = -1;
        private static readonly int IdOldMin = Shader.PropertyToID("_GiOldMin");

        // The four volumes in ONE texture, stacked along Y (volume c = rows c x NY..; the six facing blocks
        // along z): the shader reads a volume by an offset instead of choosing among four textures (its
        // compile time; see LoadAmb). Along y and not z: D3D11 allows a 3D texture 2048 a side, and the
        // facing blocks of four volumes stacked along z are 2304 deep - that failed to create and crashed the
        // game in the driver (2026-10-10).
        private static RenderTexture _amb;
        // A copy of it for the window's move (REFILL) to read: that pass writes new cells into _amb in place and
        // seeds them from the next coarser volume, and a texture cannot be read while it is written (D3D11
        // hands the shader nothing). With one texture per volume the read was another texture; stacked, it was
        // the same one, and in 0.24.0 every cell coming into a window started empty.
        private static RenderTexture _ambRead;
        private static bool _refillCopied;       // this frame's copy is made
        // What the picture reads with "Blend Neighbouring Light" on (Air Cells): _amb with each cell's light
        // averaged with the cells around it (the shader's CELLJOIN + CELLBLEND; Tools/gisix/cellblendtest.py),
        // the layers just redone blended again after every update. The update itself goes by _amb throughout
        // - a cell's history is its own light - so _GiAmb is _amb while the update's passes run and this copy
        // once they are done. _join = which cells each cell of the slab is blended with.
        private static RenderTexture _ambS, _join;
        private static int _pJoin = -1, _pBlend = -1;
        private static bool _blendTried, _blendOk;
        private static bool _blendNow;           // this update keeps the copy
        private static bool _blendLive;          // the picture is reading it
        private static readonly int IdJoin = Shader.PropertyToID("_GiJoin"), IdCellBlend = Shader.PropertyToID("_GiCellBlend");

        private static void DropBlend()
        {
            if (_ambS != null) { _ambS.Release(); Object.Destroy(_ambS); _ambS = null; }
            if (_join != null) { _join.Release(); Object.Destroy(_join); _join = null; }
            _blendTried = _blendOk = _blendNow = _blendLive = false;
            if (_amb != null) Shader.SetGlobalTexture(IdAmb, _amb);
        }
        // Where each cell's light comes from (the shader's _GiCellAim: up to three lobes a cell, "Aim Light
        // Rays" in Air Cells): the four volumes stacked along y as in _amb, one block (a cell's lobes serve all
        // six facings). Nothing clears it when a window moves or the volumes empty: a cell that has not been
        // redone since it came in is known by its age in _amb, and goes by the coarser cell's lobes till then.
        private static RenderTexture _aim, _aimScratch;
        private static bool _aimTried, _aimOk;
        private static int _pCellAim = -1;
        private static readonly int IdCellAim = Shader.PropertyToID("_GiCellAim"), IdCellAimOn = Shader.PropertyToID("_GiCellAimOn"), IdCellRays = Shader.PropertyToID("_GiCellRays");
        private static int Base(int c) => c * NY;
        private static int _pZero = -1;

        private class Grid
        {
            public RenderTexture amb;         // = _amb (kept for the readers that take a texture); a torus: cell c at texel c mod GN (the shader's Wrap)
            public Vector3Int min;            // lowest cell of the window, which follows the camera every frame (Track)
            public int layer, sweeps;
        }

        private static readonly Grid[] _g = { new Grid(), new Grid(), new Grid(), new Grid() };
        private static readonly Vector4[] _pcMin = new Vector4[Levels];
        private static RenderTexture _ray, _state;
        private static Material _mat;
        private static CommandBuffer _cb;
        private static int _turn;
        private static bool _fresh = true;
        private static Light _sun;
        internal static Light Sun => _sun;   // (GiRt's ray-traced sun shadows go into its shadow mask)
        private static readonly System.Random Rng = new System.Random(7);   // not the game's random stream

        private static readonly int IdAmb = Shader.PropertyToID("_GiAmb");
        private static readonly int IdPcMin = Shader.PropertyToID("_GiPcMin");
        private static readonly int IdRay = Shader.PropertyToID("_GiRay");
        private static readonly int IdState = Shader.PropertyToID("_GiState");
        private static readonly int IdUpdate = Shader.PropertyToID("_GiUpdate");
        private static readonly int IdNewMin = Shader.PropertyToID("_GiNewMin");
        private static readonly int IdSlice = Shader.PropertyToID("_GiSlice");
        private static readonly int IdRayRot = Shader.PropertyToID("_GiRayRot");
        private static readonly int IdSunDir = Shader.PropertyToID("_GiSunDir");
        private static readonly int IdSunColor = Shader.PropertyToID("_GiSunColor");

        private static readonly int IdLookup = Shader.PropertyToID("_GiLookup"), IdWriteLevel = Shader.PropertyToID("_GiWriteLevel");
        // REFILL stores into the volumes directly, which needs the GPU to take such writes to their format (every D3D11 card should).
        private static readonly bool UavOk = SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf);

        private static RenderTexture Flat(string name, int w, int h, RenderTextureFormat f)
        {
            var rt = new RenderTexture(w, h, 0, f, RenderTextureReadWrite.Linear)
                { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            rt.Create();
            return rt;
        }

        // Every volume has been redone twice: the second time its surfaces were lit by the first. (The 1 m volume
        // is not waited for: until it has been, the lookups pass it by.)
        // The light volumes themselves (GiRt's "Ray Traced Light Without Cells" test makes a blank one its size).
        internal static RenderTexture LightVolume => _amb;
        // Each volume's window in cells (across, high, deep), for GiRt's open-cell texture (CELLOPEN).
        internal static int CellsX => NX;
        internal static int CellsY => NY;
        internal static int CellsZ => NZ;

        public static bool Ready => !_fresh && _g[0].sweeps >= 2 && _g[2].sweeps >= 2 && _g[3].sweeps >= 2;

        // New raid: nothing the volumes hold belongs to this map.
        public static void Reset()
        {
            _fresh = true;
            _sun = null;
        }

        private static bool Ensure()
        {
            if (_mat == null) _mat = new Material(GiAssets.Probes) { hideFlags = HideFlags.DontSave };
            if (_cb == null) _cb = new CommandBuffer { name = "TraceSix Light Volumes" };
            Shader.SetGlobalVector(IdGrid, new Vector4(NX, NY, NZ, Fade));
            if (_ray == null) GiStartup.Note("the light volumes' working targets made");
            if (_ray == null)
            {
                _ray = new RenderTexture(NX * Slab * RayW, NY * RayH, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)   // 24: its stencil marks the cells that shoot
                    { name = "TraceSix Rays", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                _ray.Create();
                Shader.SetGlobalTexture(IdRay, _ray);
            }
            if (_state == null)
            {
                _state = new RenderTexture(NX * Slab, NY, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                    { name = "TraceSix Cell States", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                _state.Create();
                Shader.SetGlobalTexture(IdState, _state);
            }
            if (_amb == null)
            {
                _amb = Volume("TraceSix Light");
                _fresh = true;
                if (!_amb.IsCreated())
                {
                    Plugin.MyLog.LogError("[TraceSix] The light volumes' texture (" + NX + " x " + NY * Levels + " x " + NZ * 6 + ") could not be created: no indirect light.");
                    _ambFailed = true;
                }
            }
            if (_ambFailed) return false;
            if (_ambRead == null) _ambRead = Volume("TraceSix Light (window move)");
            for (int c = 0; c < Levels; c++) _g[c].amb = _amb;
            if (_scratch == null)
            {
                _scratch = new RenderTexture(NX, NY, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = "TraceSix Slab", dimension = TextureDimension.Tex3D, volumeDepth = Slab * 6,
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
                };
                _scratch.Create();
            }
            if (_refillRT == null) _refillRT = Flat("TraceSix Window Move", NX, NY * NZ, RenderTextureFormat.R8);
            if (!_aimTried)
            {
                _aimTried = true;
                _pCellAim = _mat.FindPass("CELLAIM");
                _aimOk = false;
                if (_pCellAim >= 0 && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB64))
                {
                    _aim = new RenderTexture(NX, NY * Levels, 0, RenderTextureFormat.ARGB64, RenderTextureReadWrite.Linear)
                    {
                        name = "TraceSix Cell Aim", dimension = TextureDimension.Tex3D, volumeDepth = NZ, enableRandomWrite = AimUavOk,   // CELLPUT writes cells in place
                        filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
                    };
                    _aim.Create();
                    _aimOk = _aim.IsCreated();
                }
                if (!_aimOk) Plugin.MyLog.LogWarning("[TraceSix] Aim Light Rays: the cells' texture could not be made (or the bundle has no CELLAIM pass): the cells' rays are spread evenly.");
            }
            if (_aimOk && _aimScratch == null)
            {
                _aimScratch = new RenderTexture(NX, NY, 0, RenderTextureFormat.ARGB64, RenderTextureReadWrite.Linear)
                {
                    name = "TraceSix Cell Aim Slab", dimension = TextureDimension.Tex3D, volumeDepth = Mathf.Max(Slab, 2),   // (a 3D texture one slice deep is refused by some drivers)
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
                };
                _aimScratch.Create();
                if (!_aimScratch.IsCreated()) _aimOk = false;
            }
            if (_aimOk) Shader.SetGlobalTexture(IdCellAim, _aim);
            // The listed cells' targets and the list itself (FillPick).
            if (!_scatterTried)
            {
                _scatterTried = true;
                _pLightAll = _mat.FindPass("LIGHTALL");
                _pPut = _mat.FindPass("CELLPUT");
                _pBlendPut = _mat.FindPass("CELLBLENDPUT");
                _scatterOk = UavOk && _pLightAll >= 0 && _pPut >= 0 && _pBlendPut >= 0 && (!_aimOk || AimUavOk);
                if (_scatterOk)
                {
                    int w = NX * Slab;
                    _litIds = new RenderTargetIdentifier[6];
                    for (int f = 0; f < 6; f++)
                    {
                        _lit[f] = Flat("TraceSix Slab Light " + f, w, NY, RenderTextureFormat.ARGBHalf);
                        _litIds[f] = _lit[f];
                        _scatterOk &= _lit[f].IsCreated();
                    }
                    _aimFlat = Flat("TraceSix Slab Aim", w, NY, RenderTextureFormat.ARGB64);
                    _putRT = Flat("TraceSix Slab Put", w, NY, RenderTextureFormat.R8);
                    _pick = new Texture2D(w, NY, TextureFormat.RGBA32, false, true)
                        { name = "TraceSix Redo List", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                    _pickBytes = new byte[w * NY * 4];
                    for (int i = 3; i < _pickBytes.Length; i += 4) _pickBytes[i] = 255;
                    // each column's own height in the order (fixed: the same cells meet in a redo every sweep)
                    var rng = new System.Random(11);
                    _colOff = new byte[NX * NY];
                    for (int i = 0; i < _colOff.Length; i++) _colOff[i] = (byte)rng.Next(NZ);
                    _colTex = new Texture2D(NX, NY, TextureFormat.R8, false, true)
                        { name = "TraceSix Redo Order", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                    _colTex.SetPixelData(_colOff, 0);
                    _colTex.Apply(false, false);
                }
                Plugin.MyLog.LogInfo(_scatterOk
                    ? "[TraceSix] Light updates: scattered - of every column one cell a redo, cells that have just come into reach first."
                    : "[TraceSix] Light updates: one layer after another (the bundle has no CELLPUT pass, or this GPU cannot write to the cells' textures in place).");
            }
            // The blended copy: made when first asked for, let go when the setting is off.
            bool wantBlend = UavOk;
            if (!wantBlend && _ambS != null) DropBlend();
            if (wantBlend && !_blendTried)
            {
                _blendTried = true;
                _pJoin = _mat.FindPass("CELLJOIN");
                _pBlend = _mat.FindPass("CELLBLEND");
                if (_pJoin >= 0 && _pBlend >= 0)
                {
                    _ambS = Volume("TraceSix Light (blended)");
                    _blendOk = _ambS.IsCreated();
                }
                if (!_blendOk) Plugin.MyLog.LogWarning("[TraceSix] Blend Neighbouring Light: the copy of the light volumes could not be made (or the bundle has no CELLBLEND pass): each cell shows its own light.");
                else Plugin.MyLog.LogInfo("[TraceSix] Blend Neighbouring Light: the picture reads a blended copy of the light volumes (" + (NX * (float)NY * Levels * NZ * 6 * 8 / (1024f * 1024f)).ToString("F0") + " MB).");
            }
            if (_blendOk && _join == null)
            {
                _join = Flat("TraceSix Cell Joins", NX * Slab, NY, RenderTextureFormat.ARGB32);
            }
            if (_pRefill < 0) _pRefill = _mat.FindPass("REFILL");
            if (_pMark < 0) _pMark = _mat.FindPass("RAYMARK");
            if (_pZero < 0) _pZero = _mat.FindPass("ZERO");
            if (!_fresh) return true;
            GiStartup.Note("the light volumes emptied for a fresh fill");

            // Empty volumes: a cell with alpha 0 counts for nothing, so an unfilled volume hands over to the
            // next coarser one and finally to the plain sky ambient.
            _cb.Clear();
            _cb.SetRenderTarget(new RenderTargetIdentifier(_amb, 0, CubemapFace.Unknown, RenderTargetIdentifier.AllDepthSlices));
            _cb.ClearRenderTarget(false, true, Color.clear);
            _blendLive = false;                              // the blended copy starts over from this (Tick)
            for (int c = 0; c < Levels; c++)
            {
                Grid g = _g[c];
                g.layer = 0;
                g.sweeps = 0;
                g.min = Vector3Int.zero;
                Publish(c);
            }
            Graphics.ExecuteCommandBuffer(_cb);
            Shader.SetGlobalTexture(IdRay, _ray);
            Shader.SetGlobalTexture(IdState, _state);
            Shader.SetGlobalMatrixArray("_GiIndoor", _indoor);
            Shader.SetGlobalFloat("_GiIndoorCount", _indoorCount);
            PublishIndoorIndex();
            _turn = 0;
            _wake = Vector4.zero;
            _wakeNext.Clear();
            _wakeNextLamp.Clear();
            _wakeLamp = false;
            _rushFull = false;
            System.Array.Clear(_rushLeft, 0, Levels);
            _fresh = false;
            return true;
        }
        private static bool _ambFailed;

        // The 1 m volume starts empty: an empty volume is passed by (alpha 0 in every cell).
        private static void EmptyBetween()
        {
            Grid g = _g[1];
            if (g.amb == null) return;
            _cb.Clear();
            for (int z = 0; z < 6 * NZ; z++)                 // its rows of the stack alone, slice by slice
            {
                _cb.SetRenderTarget(new RenderTargetIdentifier(_amb, 0, CubemapFace.Unknown, z));
                _cb.SetViewport(new Rect(0, Base(1), NX, NY));
                _cb.DrawProcedural(Matrix4x4.identity, _mat, _pZero, MeshTopology.Triangles, 3);
            }
            Graphics.ExecuteCommandBuffer(_cb);
            _blendLive = false;                              // ... and the blended copy with it (Tick)
            g.layer = 0;
            g.sweeps = 0;
            g.min = Vector3Int.zero;
            Publish(1);
        }

        // Every redo in progress starts over (what a cost-test set-up left half done is not carried on).
        public static void Restart()
        {
            foreach (Grid g in _g) g.layer = 0;
        }

        // The light volumes empty and fill again (the voxel world they were lit through has been replaced).
        public static void Refill() => _fresh = true;

        // How many z layers of a volume are redone per frame. The two targets a slab is worked out in are
        // that wide, so they are made anew; every redo in progress starts over.
        public static void SetSlab(int layers)
        {
            if (layers == Slab || layers < 1 || NZ % layers != 0) return;
            Slab = layers;
            if (_ray != null) { _ray.Release(); Object.Destroy(_ray); _ray = null; }
            if (_state != null) { _state.Release(); Object.Destroy(_state); _state = null; }
            if (_join != null) { _join.Release(); Object.Destroy(_join); _join = null; }
            DropScratch();
            foreach (Grid g in _g) g.layer = 0;
        }

        // Per volume six blocks of NZ slices (light for surfaces facing +x, -x, +y, -y, +z, -z), the four volumes one after another.
        private static RenderTexture Volume(string name)
        {
            var rt = new RenderTexture(NX, NY * Levels, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name, dimension = TextureDimension.Tex3D, volumeDepth = NZ * 6, enableRandomWrite = UavOk,   // REFILL writes cells in place
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
            };
            rt.Create();
            return rt;
        }

        private static void Publish(int c)
        {
            Grid g = _g[c];
            Shader.SetGlobalTexture(IdAmb, _amb);
            _pcMin[c] = new Vector4(g.min.x, g.min.y, g.min.z, CellSize[c]);
            Shader.SetGlobalVectorArray(IdPcMin, _pcMin);
        }

        // Every frame in a raid, after the voxel world's own tick.
        public static void Tick(Camera cam)
        {
            if (!GiConfig.On || GiTest.Freeze || !VoxelScene.AllReady || GiAssets.Probes == null) return;
            if (!Ensure()) return;
            // "Lighter Light Cells": while the ray-traced light lights the picture the cells only answer the third
            // bounce on and the rays that meet nothing - updated every other frame, and not blended (below)
            bool lighter = GiRt.Applying && !GiTest.RtFullCells;
            if (lighter && (Time.frameCount & 1) == 1) return;
            if (GiConfig.Between.Value != _between)
            {
                _between = GiConfig.Between.Value;
                EmptyBetween();
            }
            // Whose turn: while the volumes are first filling, the one in use that is furthest behind (so the
            // picture can start as soon as all are done twice, whatever their place in the round); then the round.
            int c = -1;
            if (!Ready || (_between && _g[1].sweeps < 2))
            {
                int least = int.MaxValue;
                for (int k = 0; k < Levels; k++)
                {
                    if ((k == 1 && !_between) || _g[k].sweeps >= 2) continue;
                    int done = _g[k].sweeps * NZ + _g[k].layer;
                    if (done < least) { least = done; c = k; }
                }
                // ... but the volumes the picture is already lit by keep every other turn once they are done
                if (Ready && (_fillTurn++ & 1) == 0) c = -1;
            }
            if (c < 0) c = _between ? OrderWith[_turn++ & 7] : OrderWithout[_turn++ & 3];
            Grid g = _g[c];
            Vector3 cp = cam.transform.position;
            UpdateSun();
            PickIndoor(cp);
            Shader.SetGlobalFloat(IdLookup, 1f);

            _cb.Clear();
            _refillCopied = false;
            // The update reads each cell's own light; the picture, after it, the blended copy where one is kept.
            _blendNow = _blendOk && !lighter;
            _cb.SetGlobalTexture(IdAmb, _amb);
            _cb.SetGlobalFloat(IdCellBlend, _blendNow ? 1f : 0f);
            if (_blendNow && !_blendLive) _cb.CopyTexture(_amb, _ambS);         // just begun: as stored, each layer blended at its next turn
            // the cells of this redo: listed (scattered, the new ones first) or one layer after another
            _scatterNow = _scatterOk;
            if (!_scatterNow) foreach (Queue<int> q in _newCells) q.Clear();
            _cb.SetGlobalFloat(IdNoHit, GiTest.NoHit);
            _cb.SetGlobalFloat(IdHitNearest, !GiTest.HitFull ? 1f : 0f);
            for (int l = 0; l < Levels; l++) if (l != 1 || _between) Track(l, cp);   // the windows follow the camera
            // The list is sent only when it has cells that have just come into reach (after Track, which finds
            // them); in any other redo the shader works the same order out from _colTex. (Sending it every
            // frame cost 0.14 ms a frame - his cost log of 0.24.43 - against 0.03 ms of this code.)
            if (_scatterNow && _rushLeft[c] > 0 && _newCells[c].Count == 0)
            {
                EnqueueRush(c);
                _rushLeft[c]--;
            }
            bool listed = _scatterNow && _newCells[c].Count > 0;
            if (listed)
            {
                FillPick(c, g);
                _cb.SetGlobalTexture(IdPick, _pick);
            }
            if (_scatterNow) _cb.SetGlobalTexture(IdColOff, _colTex);
            _cb.SetGlobalFloat(IdPickOn, listed ? 1f : _scatterNow ? 2f : 0f);
            _cb.SetGlobalVector(IdUpdate, new Vector4(c, History, g.layer, g.sweeps));
            // w: 1 = a young cell holds the mean of its sweeps, 2 = and starts from what the next coarser volume
            // holds (once that one is filled)
            int coarser = c == 0 && !BetweenInUse ? 2 : c + 1;
            float young = coarser < Levels && _g[coarser].sweeps >= 2 ? 2f : 1f;
            _cb.SetGlobalFloat(IdLevels, BetweenInUse ? 4f : 3f);
            _cb.SetGlobalFloat(IdRest, 1f);
            // the cells' rays aimed at where each cell's light comes from, and how many a cell shoots
            bool aim = _aimOk;
            _cb.SetGlobalFloat(IdCellAimOn, aim ? 1f : 0f);
            _cb.SetGlobalFloat(IdCellRays, GiConfig.CellRays.Value == 16 ? 16f : RayW * RayH);
            if (_wake.w > 0f && _g[0].sweeps >= _wakeUntil)
            {
                _wake = Vector4.zero;
                if (_wakeNext.Count > 0)
                {
                    Vector4 next = _wakeNext[0];
                    bool lampNext = _wakeNextLamp.Count > 0 && _wakeNextLamp[0];
                    _wakeNext.RemoveAt(0);
                    if (_wakeNextLamp.Count > 0) _wakeNextLamp.RemoveAt(0);
                    StartWake(next, lampNext);
                    if (lampNext) RushLamp(next, LampRush(next.w / 3f));   // (its sphere was 3 x the reach)
                    else Rush(next);
                }
            }
            _cb.SetGlobalVector(IdWake, _wake);
            // x: the running wake is a lamp's (a settled cell in it starts over), y: within a moment of the switch (every
            // woken cell does: a lamp flicked off and on again is as quick the second time)
            _cb.SetGlobalVector(IdWakeLamp, new Vector4(_wake.w > 0f && _wakeLamp ? 1f : 0f, Time.unscaledTime < _lampResetUntil ? 1f : 0f, 0f, 0f));
            if (_rushFull && _rushLeft[0] <= 0 && _rushLeft[1] <= 0 && _newCells[0].Count == 0 && _newCells[1].Count == 0) _rushFull = false;
            _cb.SetGlobalVector(IdNewMin, new Vector4(g.min.x, g.min.y, g.min.z, young));
            _cb.SetGlobalMatrix(IdRayRot, Matrix4x4.Rotate(RandomRotation()));
            // (SixBench's detail run, GiTest: the rays shot a second time on the same inputs, or the stages after
            // one left out - or all of it worked out and none run)
            int upTo = GiTest.VolUpTo;
            bool whole = upTo <= 1;
            _cb.SetGlobalFloat(IdRayHold, BudgetOn ? _hold[c] : 0f);
            _cb.SetRenderTarget(_state);                                         // what each cell of the slab does
            _cb.DrawProcedural(Matrix4x4.identity, _mat, PassState, MeshTopology.Triangles, 3);
            ReadStates(c);
            if (whole || upTo >= 3) MarkRays();
            for (int again = !whole && upTo < 4 ? 0 : GiTest.VolTwice == 2 ? 2 : 1; again > 0; again--)
            {
                _cb.SetRenderTarget(_ray);                                       // its rays, where a cell shoots
                _cb.DrawProcedural(Matrix4x4.identity, _mat, PassTrace, MeshTopology.Triangles, 3);
            }
            if (_scatterNow)
            {
                for (int again = !whole ? 0 : 1; again > 0; again--)
                {
                    _cb.SetRenderTarget(_litIds, _lit[0]);                       // all six facings of the listed cells
                    _cb.DrawProcedural(Matrix4x4.identity, _mat, _pLightAll, MeshTopology.Triangles, 3);
                }
                if (aim && whole)
                {
                    _cb.SetRenderTarget(_aimFlat);                               // their lobes after this redo
                    _cb.DrawProcedural(Matrix4x4.identity, _mat, _pCellAim, MeshTopology.Triangles, 3);
                }
                for (int again = !whole ? 0 : 1; again > 0; again--)
                {
                    for (int f = 0; f < 6; f++) _cb.SetGlobalTexture(IdLit[f], _lit[f]);
                    _cb.SetGlobalTexture(IdAimNew, _aimFlat);
                    _cb.SetGlobalFloat(IdPutAim, aim ? 1f : 0f);
                    _cb.SetRenderTarget(_putRT);                                 // ... each to its own place in the volume
                    _cb.SetRandomWriteTarget(3, _amb);
                    if (aim) _cb.SetRandomWriteTarget(5, _aim);
                    _cb.DrawProcedural(Matrix4x4.identity, _mat, _pPut, MeshTopology.Triangles, 3);
                    _cb.ClearRandomWriteTargets();
                }
                if (_blendNow && whole)
                {
                    _cb.SetRenderTarget(_join);                                  // which cells each of them is blended with
                    _cb.DrawProcedural(Matrix4x4.identity, _mat, _pJoin, MeshTopology.Triangles, 3);
                    _cb.SetGlobalTexture(IdJoin, _join);
                    _cb.SetRenderTarget(_putRT);                                 // ... and their places in the blended copy
                    _cb.SetRandomWriteTarget(4, _ambS);
                    _cb.DrawProcedural(Matrix4x4.identity, _mat, _pBlendPut, MeshTopology.Triangles, 3);
                    _cb.ClearRandomWriteTargets();
                }
            }
            else
            {
                for (int again = !whole ? 0 : 1; again > 0; again--)
                    for (int z = g.layer; z < g.layer + Slab; z++)                   // folded into the six facings, into the slab
                        for (int f = 0; f < 6; f++)
                        {
                            _cb.SetGlobalVector(IdSlice, new Vector4(f, z, 0f, 0f));
                            _cb.SetRenderTarget(new RenderTargetIdentifier(_scratch, 0, CubemapFace.Unknown, (z - g.layer) * 6 + f));
                            _cb.DrawProcedural(Matrix4x4.identity, _mat, PassLight, MeshTopology.Triangles, 3);
                        }
                if (aim && whole)
                    for (int z = g.layer; z < g.layer + Slab; z++)                   // each cell's lobes after this redo (they read what the volume held: before the copies)
                    {
                        _cb.SetGlobalVector(IdSlice, new Vector4(0f, z, 0f, 0f));
                        _cb.SetRenderTarget(new RenderTargetIdentifier(_aimScratch, 0, CubemapFace.Unknown, z - g.layer));
                        _cb.DrawProcedural(Matrix4x4.identity, _mat, _pCellAim, MeshTopology.Triangles, 3);
                    }
                for (int again = !whole ? 0 : 1; again > 0; again--)
                    for (int z = g.layer; z < g.layer + Slab; z++)                   // ... and the slab into the volume
                        for (int f = 0; f < 6; f++)
                            _cb.CopyTexture(_scratch, (z - g.layer) * 6 + f, 0, 0, 0, NX, NY, _amb, f * NZ + z, 0, 0, Base(c));
                if (aim && whole)
                    for (int z = g.layer; z < g.layer + Slab; z++)
                        _cb.CopyTexture(_aimScratch, z - g.layer, 0, 0, 0, NX, NY, _aim, z, 0, 0, Base(c));
                if (_blendNow && whole)
                {
                    _cb.SetRenderTarget(_join);                                      // which cells each cell of the slab is blended with
                    _cb.DrawProcedural(Matrix4x4.identity, _mat, _pJoin, MeshTopology.Triangles, 3);
                    _cb.SetGlobalTexture(IdJoin, _join);
                    for (int z = g.layer; z < g.layer + Slab; z++)                   // ... and the slab's layers of the copy, blended
                        for (int f = 0; f < 6; f++)
                        {
                            _cb.SetGlobalVector(IdSlice, new Vector4(f, z, 0f, 0f));
                            _cb.SetRenderTarget(new RenderTargetIdentifier(_ambS, 0, CubemapFace.Unknown, f * NZ + z));
                            _cb.DrawProcedural(Matrix4x4.identity, _mat, _pBlend, MeshTopology.Triangles, 3);
                        }
                }
            }
            _cb.SetGlobalTexture(IdAmb, _blendNow ? _ambS : _amb);               // what the picture reads
            if (upTo != 1)
            {
                Graphics.ExecuteCommandBuffer(_cb);
                _blendLive = _blendNow;
            }
            g.layer += Slab;
            if (g.layer < NZ) return;
            g.layer = 0;
            g.sweeps++;
            if (g.sweeps == 6 || (GiConfig.DebugLog.Value && g.sweeps % 300 == 0)) LogContents(c, _amb);
        }

        // The ray target's stencil: 1 on the ray pixels of the cells STATE has shooting this time (TraceSixProbes
        // RAYMARK). TRACE is drawn there only, so a resting cell costs nothing in it.
        private static int _pMark = -1;

        private static void MarkRays()
        {
            if (_pMark < 0) return;                                              // (a pass of -1 would draw every pass)
            _cb.SetRenderTarget(_ray);
            _cb.ClearRenderTarget(true, false, Color.clear);                     // the stencil (with the depth), not the rays
            _cb.DrawProcedural(Matrix4x4.identity, _mat, _pMark, MeshTopology.Triangles, 3);
        }

        // A volume's window, centred on the camera to the cell, every frame. When it moves, the texels now
        // holding cells the old window did not are refilled from the coarser volume (REFILL), in _cb.
        private static void Track(int l, Vector3 cp)
        {
            Grid g = _g[l];
            float S = CellSize[l];
            var want = new Vector3Int(Mathf.RoundToInt(cp.x / S) - NX / 2, Mathf.RoundToInt(cp.y / S) - NY / 2, Mathf.RoundToInt(cp.z / S) - NZ / 2);
            if (want == g.min) return;
            Vector3Int old = g.min;
            g.min = want;
            Publish(l);
            if (_scatterNow) EnqueueNew(l, old, want);
            if (_pRefill < 0) return;
            _cb.SetGlobalVector(IdOldMin, new Vector4(old.x, old.y, old.z, 0f));
            _cb.SetGlobalFloat(IdWriteLevel, l);
            // read the copy, write the texture itself (one copy a frame, only in frames where a window moves)
            if (!_refillCopied && _ambRead != null && _ambRead.IsCreated())
            {
                _cb.CopyTexture(_amb, _ambRead);
                _refillCopied = true;
            }
            if (_refillCopied) _cb.SetGlobalTexture(IdAmb, _ambRead);
            _cb.SetRenderTarget(_refillRT);
            _cb.SetRandomWriteTarget(3, _amb);
            if (_blendNow) _cb.SetRandomWriteTarget(4, _ambS);                   // the blended copy takes the same new cells
            _cb.DrawProcedural(Matrix4x4.identity, _mat, _pRefill, MeshTopology.Triangles, 3);
            _cb.ClearRandomWriteTargets();
            _cb.SetGlobalTexture(IdAmb, _amb);
        }

        // A uniformly random rotation (Shoemake): each update looks along a different set of 32 directions.
        private static Quaternion RandomRotation()
        {
            double u = Rng.NextDouble(), a = Rng.NextDouble() * 2.0 * System.Math.PI, b = Rng.NextDouble() * 2.0 * System.Math.PI;
            float p = Mathf.Sqrt((float)(1.0 - u)), q = Mathf.Sqrt((float)u);
            return new Quaternion(p * Mathf.Sin((float)a), p * Mathf.Cos((float)a), q * Mathf.Sin((float)b), q * Mathf.Cos((float)b));
        }

        //--- "the spot ahead" --------------------------------------------------------------------------------

        // Debug, for light that is wrong at a distance (a bright wall at the far end of a corridor): what the
        // picture's lookup does for the surface under the crosshair, redone on the CPU from the textures the GPU
        // reads (TraceSixProbes.shader: KindAt, Level). The surface's own mark; in every light volume in use each
        // point the search tries and why its cell is refused, the cell taken, what it holds and how much sky its
        // centre sees; the cells around; then the voxels above and below, and the meshes there.
        public static void Spot(Camera cam)
        {
            RaycastHit hit = default;
            float best = float.MaxValue;
            Transform self = cam.transform.root;
            // The nearest collider past arm's length that is not the player's own.
            foreach (RaycastHit h in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 300f, ~0, QueryTriggerInteraction.Ignore))
                if (h.distance > 1.5f && h.distance < best && !h.collider.transform.IsChildOf(self)) { best = h.distance; hit = h; }
            if (best == float.MaxValue) { Plugin.MyLog.LogInfo("[TraceSix] Spot: nothing under the crosshair between 1.5 and 300 m."); return; }
            Vector3 p = hit.point, n = hit.normal, eye = cam.transform.position;
            string name = hit.collider.name;
            int sky = Mathf.RoundToInt((SkyAmbientUp().x + SkyAmbientUp().y + SkyAmbientUp().z) / 3f * 1000f);
            bool steady = true, sight = true;   // ("Steady Lookup", "Light Cells By Sight": always on since 0.24.148)
            VoxelScene.ReadOpacity(0, (o0, s0, v0) => VoxelScene.ReadOpacity(1, (o1, s1, v1) => VoxelScene.ReadOpacity(2, (o2, s2, v2) => VoxelScene.ReadOpacity(3, (o3, s3, v3) =>
                VoxelScene.ReadFaces(1, f1 => VoxelScene.ReadFaces(2, f2 =>
            {
                var vox = new Vox { org = new[] { o0, o1, o2, o3 }, size = new[] { s0, s1, s2, s3 }, at = new[] { v0, v1, v2, v3 }, pos = new[] { null, f1, f2, null } };
                var sb = new System.Text.StringBuilder();
                sb.Append("[TraceSix] Spot: '").Append(name).Append("' at ").Append(p.ToString("F1")).Append(", ").Append(best.ToString("F1")).Append(" m away, facing ")
                  .Append(n.ToString("F2")).Append("; the camera at ").Append(eye.ToString("F1")).Append(". Open sky gives ").Append(sky)
                  .Append(" (light numbers are in the game's kind of number x1000). Not redone here: a cell of the other kind taken where the surface sees its centre, ")
                  .Append("and the mark and first steps stopping short of what is in front of the surface - the mark and the searches below are the old rule's; ")
                  .Append("the view Show Which Probes Answer shows the mark the picture uses.");

                // The surface's own mark (the shader's KindAt).
                Vector3 toEye = eye - p;
                float reach = toEye.magnitude;
                toEye /= Mathf.Max(reach, 1e-4f);
                Vector3 m = n + toEye;
                m = steady || m.sqrMagnitude <= 1e-4f ? n : m.normalized;
                bool box = IndoorCpu(p + n * (0.5f * CellSize[0]));
                vox.At(p, out float vsP);
                float off = 1.5f * Mathf.Max(vsP, CellSize[0]);
                float left = vox.Up(p + m * off, false, out float stop);
                bool indoor = box && left <= 0.5f;
                sb.Append("\n  The surface's own mark (which cells the 1 m, 2 m and 8 m volumes may give it): ").Append(indoor ? "INDOOR" : "OUTDOOR")
                  .Append(". Inside one of the game's interior volumes: ").Append(box ? "yes" : "NO").Append("; straight up from ").Append(off.ToString("F2"))
                  .Append(" m off it (the surface is in ").Append(vsP > 0f ? vsP + " m voxels" : "no voxel volume").Append("): ")
                  .Append(stop >= 0f ? "stopped " + stop.ToString("F1") + " m up" : "nothing stops it, " + (100f * left).ToString("F0") + "% of the ray left").Append('.');

                var w = new float[Levels];
                System.Action<int> level = null;
                level = c =>
                {
                    if (c == Levels)
                    {
                        try { SpotEnd(p, n, w, indoor, vox, sb); }
                        catch (System.Exception e) { Plugin.MyLog.LogWarning("[TraceSix] Spot failed: " + e); }
                        return;
                    }
                    Grid g = _g[c];
                    if (g.amb == null || (c == 1 && !BetweenInUse))
                    {
                        sb.Append("\n  ").Append(CellSize[c]).Append(" m light volume: not in use.");
                        level(c + 1);
                        return;
                    }
                    Vector3Int min = g.min;      // the window as it is in the frame the texture is read
                    AsyncGPUReadback.Request(_blendLive ? _ambS : _amb, 0, 0, NX, Base(c), NY, 0, NZ * 6, TextureFormat.RGBAHalf, req =>
                    {
                        try { w[c] = SpotLevel(c, min, req, p, n, m, eye, indoor, sight, vox, sb); }
                        catch (System.Exception e) { sb.Append("\n  ").Append(CellSize[c]).Append(" m light volume: failed: ").Append(e.Message); }
                        level(c + 1);
                    });
                };
                level(0);
            }))))));
        }

        // One light volume's part of Spot: the shader's Level for that surface. Returns the weight it answers with.
        private static float SpotLevel(int c, Vector3Int min, AsyncGPUReadbackRequest req, Vector3 p, Vector3 n, Vector3 m, Vector3 eye, bool indoor, bool sight,
            Vox vox, System.Text.StringBuilder sb)
        {
            float S = CellSize[c];
            sb.Append("\n  ").Append(S).Append(" m light volume").Append(_blendLive ? " (as the picture reads it: each cell blended with those around it)" : "").Append(": ");
            if (req.hasError) { sb.Append("readback failed."); return 0f; }
            int bx = n.x >= 0f ? 0 : 1, by = n.y >= 0f ? 2 : 3, bz = n.z >= 0f ? 4 : 5;
            // A cell by its place in the world: the texture is a torus (the shader's Wrap).
            float Load(Vector3Int a, int block, int ch)
            {
                int x = ((a.x % NX) + NX) % NX, y = ((a.y % NY) + NY) % NY, z = ((a.z % NZ) + NZ) % NZ;
                return Mathf.HalfToFloat(req.GetData<ushort>(block * NZ + z)[(y * NX + x) * 4 + ch]);
            }
            // What one cell alone would give this surface (the shader's Shade), as a number of the game's.
            int Lit(Vector3Int a)
            {
                float lin = 0f;
                for (int ch = 0; ch < 3; ch++) lin += Load(a, bx, ch) * n.x * n.x + Load(a, by, ch) * n.y * n.y + Load(a, bz, ch) * n.z * n.z;
                return Mathf.RoundToInt(Mathf.Pow(Mathf.Max(lin / (3f * Store), 0f), 1f / 2.2f) * 1000f);
            }
            Vector3 Centre(Vector3Int a) => ((Vector3)a + Vector3.one * 0.5f) * S;
            bool Held(Vector3 at, float margin)
            {
                Vector3 g = at / S - (Vector3)min;
                return g.x >= margin && g.y >= margin && g.z >= margin && g.x < NX - margin && g.y < NY - margin && g.z < NZ - margin;
            }

            Vector3 away = eye - p;
            float reach = away.magnitude;
            Vector3 toEye = away / Mathf.Max(reach, 1e-4f);
            var edge = new Vector3(0.5f * NX - 2f - Mathf.Abs(away.x) / S, 0.5f * NY - 2f - Mathf.Abs(away.y) / S, 0.5f * NZ - 2f - Mathf.Abs(away.z) / S);
            float weight = Mathf.Clamp01(Mathf.Min(Mathf.Min(edge.x, edge.z) * Fade, edge.y * 0.25f));
            sb.Append("by distance it would answer with weight ").Append(weight.ToString("F2")).Append('.');

            bool any = c == 0;                   // the 0.5 m volume takes cells of either mark
            bool found = false, outside = false;
            Vector3Int home = default;
            sb.Append("\n    The search: off the surface by");
            for (int k = 1; k <= 16; k++)
            {
                float along = 0.5f * S * (k - 4);
                Vector3 at = k <= 4 ? p + m * (0.5f * S * k) : p + toEye * Mathf.Min(along, reach);
                if (!Held(at, 1f)) { outside = true; break; }
                if (k == 5) sb.Append("; then toward the camera by");
                Vector3Int a = Fl3(at / S);
                float al = Load(a, bx, 3);
                bool open = al > 0.25f, front = any || Vector3.Dot(Centre(a) - p, n) > 0f, fits = any || (al < 0.8f) == indoor;
                sb.Append(k == 1 || k == 5 ? " " : ", ").Append((k <= 4 ? 0.5f * S * k : Mathf.Min(along, reach)).ToString("0.#")).Append(" m ")
                  .Append(!open ? vox.WhyDead(Centre(a)) : !front ? "behind" : fits ? "TAKEN" : al < 0.8f ? "indoor cell" : "outdoor cell");
                if (open && front && fits) { found = true; home = a; break; }
                if (along >= reach) break;
            }
            sb.Append('.');
            if (outside) { sb.Append(" That point is outside this volume: it gives nothing."); return 0f; }
            if (!found && c == 0)
            {
                sb.Append("\n    No open cell on the way: the 0.5 m volume then reads off the voxel's faces (not redone here; \"Log Light Lookup Under Me\" does).");
                return weight;
            }
            Vector3 first = p + m * (0.5f * S);
            if (!found)
            {
                if (!Held(first, 2f)) { sb.Append(" The first point is too near this volume's edge: it gives nothing."); return 0f; }
                Vector3Int qc = Fl3(first / S);
                Vector3 q = first / S;
                int dead = 0, behind = 0, other = 0;
                float bestD = float.MaxValue;
                for (int j = 0; j < 27; j++)
                {
                    Vector3Int cl = qc + new Vector3Int(j % 3 - 1, j / 3 % 3 - 1, j / 9 - 1);
                    float al = Load(cl, bx, 3);
                    if (al <= 0.25f) { dead++; continue; }
                    if (Vector3.Dot(Centre(cl) - p, n) <= 0f) { behind++; continue; }
                    if ((al < 0.8f) != indoor) { other++; continue; }
                    Vector3 dd = Vector3.Max(Vector3.Max((Vector3)cl - q, q - ((Vector3)cl + Vector3.one)), Vector3.zero);
                    if (dd.sqrMagnitude < bestD) { bestD = dd.sqrMagnitude; home = cl; found = true; }
                }
                sb.Append("\n    None on the way. Of the 27 cells around the first point: ").Append(dead).Append(" in geometry, ").Append(behind)
                  .Append(" centred behind the surface, ").Append(other).Append(" of the other mark, ").Append(27 - dead - behind - other).Append(" it may take.");
                if (!found)
                {
                    sb.Append(" NO CELL FOR THIS SURFACE: this volume gives it nothing and the next coarser one is asked.");
                    SpotCells(S, min, first, n, Load, Lit, Held, default, false, sb);
                    return 0f;
                }
            }
            Vector3 hc = Centre(home);
            float ha = Load(home, bx, 3);
            sb.Append("\n    The cell taken is centred at ").Append(hc.ToString("F1")).Append(": ").Append((hc - p).magnitude.ToString("F1")).Append(" m from the surface, ")
              .Append((hc.y - p.y).ToString("+0.0;-0.0")).Append(" m in height. Marked ").Append(ha < 0.8f ? "INDOOR" : "OUTDOOR").Append(", holds ").Append(Lit(home))
              .Append(" for this surface, ").Append(Mathf.RoundToInt(Mathf.Pow(Mathf.Max((Load(home, 2, 0) + Load(home, 2, 1) + Load(home, 2, 2)) / (3f * Store), 0f), 1f / 2.2f) * 1000f))
              .Append(" for a floor there.");
            if (c > 0)
            {
                // The cell's own mark as the voxels have it now.
                float left = vox.Up(hc, true, out float stop);
                sb.Append(" Its centre: inside an interior volume ").Append(IndoorCpu(hc) ? "yes" : "NO").Append(", straight up ")
                  .Append(stop >= 0f ? "stopped " + stop.ToString("F1") + " m up" : "nothing stops it (" + (100f * left).ToString("F0") + "% left)").Append('.');
            }
            // How much of the sky its centre sees through the voxels, and whether the game's own collision
            // agrees that those ways out are open (a window) or has a wall there (a leak).
            sb.Append("\n    ").Append(vox.Sky(hc));
            SpotCells(S, min, first, n, Load, Lit, Held, home, true, sb);
            return weight;
        }

        // The cells around the first point off the surface, three layers of 3 x 3.
        private static void SpotCells(float S, Vector3Int min, Vector3 first, Vector3 n, System.Func<Vector3Int, int, int, float> load, System.Func<Vector3Int, int> lit,
            System.Func<Vector3, float, bool> held, Vector3Int home, bool taken, System.Text.StringBuilder sb)
        {
            Vector3Int qc = Fl3(first / S);
            int bx = n.x >= 0f ? 0 : 1;
            sb.Append("\n    Cells around the first point off the surface - dead, or in / out (marked indoor / outdoor) with what each alone would give this surface; * = the one taken:");
            for (int dy = 1; dy >= -1; dy--)
            {
                sb.Append("\n      layer ").Append(dy > 0 ? "+1" : dy.ToString()).Append(" (centred ").Append(((qc.y + dy + 0.5f) * S).ToString("F1")).Append(" m high), rows from +z to -z, columns from -x to +x:");
                for (int dz = 1; dz >= -1; dz--)
                {
                    sb.Append("\n        ");
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        Vector3Int a = qc + new Vector3Int(dx, dy, dz);
                        if (!held(((Vector3)a + Vector3.one * 0.5f) * S, 0f)) { sb.Append("--".PadRight(12)); continue; }
                        float al = load(a, bx, 3);
                        string t = al <= 0.25f ? "dead" : (al < 0.8f ? "in " : "out ") + lit(a);
                        sb.Append((t + (taken && a == home ? "*" : "")).PadRight(12));
                    }
                }
            }
        }

        // The end of Spot: who lights the surface, the voxels above and below it, the meshes there.
        private static void SpotEnd(Vector3 p, Vector3 n, float[] w, bool indoor, Vox vox, System.Text.StringBuilder sb)
        {
            // The shader's IndirectLight: the finest volume that answers, the rest to the next.
            float rest = 1f;
            sb.Append("\n  So this surface is lit:");
            for (int c = 0; c < Levels; c++)
            {
                sb.Append(' ').Append((100f * rest * w[c]).ToString("F0")).Append("% by the ").Append(CellSize[c]).Append(" m volume,");
                rest *= 1f - w[c];
            }
            sb.Append(' ').Append((100f * rest).ToString("F0")).Append("% by the plain sky ambient.");
            if (w[1] <= 0f && w[2] <= 0f && w[3] <= 0f && w[0] < 0.999f)
                sb.Append(" (No coarse volume has a cell of its mark: the shader then takes any cell, finest volume first - not redone here.)");
            sb.Append(" The view \"Show Which Probes Answer\" should agree (green / blue-green / yellow / red / blue, darker = ").Append(indoor ? "indoor: this one" : "indoor; this one is not").Append(").");

            sb.Append("\n  Voxels straight above and below the point (the finest volume holding each; coverage of 31):");
            string last = null;
            for (int k = 16; k >= -4; k--)
            {
                Vector3 q = p + Vector3.up * (k * 0.5f);
                uint u = vox.At(q, out float vs);
                string key = vs + "/" + (vs > 0f ? Mathf.FloorToInt(q.y / vs) : 0);
                if (key == last) continue;
                last = key;
                sb.Append("\n    ").Append(k >= 0 ? "+" : "").Append((k * 0.5f).ToString("F1")).Append(" m (").Append(vs > 0f ? vs + " m voxel" : "none").Append("): ").Append(VoxText(u));
            }
            VoxelScene.Explain(new[] { p + n * 0.05f, p + Vector3.up, p + Vector3.up * 2f, p + Vector3.up * 3f, p + Vector3.up * 4f, p + Vector3.up * 6f }, sb);
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        // The voxel world as the shader reads it, from read-backs of the four volumes and of the 1 m and 2 m ones' faces.
        private class Vox
        {
            public Vector3[] org;
            public float[] size;
            public System.Func<Vector3Int, uint>[] at;
            public System.Func<Vector3Int, uint>[] pos;      // per volume: where in each voxel its faces sit (null: not read; the shader's InAir goes by the 1 m and 2 m ones)

            // The shader's LevelAt + VoxelOf: the finest volume holding the point (level -1, uint.MaxValue outside all).
            public uint At(Vector3 p, out float vs, out int level, out Vector3 q)
            {
                for (int l = 0; l < at.Length; l++)
                {
                    if (at[l] == null) continue;
                    q = (p - org[l]) / size[l];
                    uint u = at[l](Fl3(q));
                    if (u != uint.MaxValue) { vs = size[l]; level = l; return u; }
                }
                vs = -1f;
                level = -1;
                q = Vector3.zero;
                return uint.MaxValue;
            }

            public uint At(Vector3 p, out float vs) => At(p, out vs, out _, out _);

            // The shader's InAir, for a point at q in the 1 m or 2 m voxel u: clear of every face that covers half the
            // voxel or more? ahead = per axis, 1 where the faces a ray along d meets are in front of the point.
            // why = the first axis it fails on.
            public bool InAir(int level, uint u, Vector3 q, Vector3 d, out Vector3 ahead, out string why)
            {
                uint pu = pos[level](Fl3(q));
                if (pu == uint.MaxValue) pu = 0u;
                ahead = Vector3.one;
                why = null;
                bool air = true;
                string[] axis = { "x", "y", "z" };
                for (int a = 0; a < 3; a++)
                {
                    float fwd = ((u >> (5 * a)) & 31) / 31f, bwd = ((u >> (15 + 5 * a)) & 31) / 31f;
                    float pf = ((pu >> (5 * a)) & 31) / 31f, pb = ((pu >> (15 + 5 * a)) & 31) / 31f;
                    float c = q[a] - Mathf.Floor(q[a]);
                    ahead[a] = d[a] > 0f ? (pb > c ? 1f : 0f) : (pf < c ? 1f : 0f);
                    bool over = c > pf + 0.125f, under = c < pb - 0.125f, f = fwd >= 0.5f, k = bwd >= 0.5f;
                    bool ok = f && k ? (pb <= pf ? over || under : over && under) : f ? over : !k || under;
                    if (!ok && why == null)
                        why = "along " + axis[a] + " the centre is at " + c.ToString("F2") + " of the voxel, " + (f ? "what faces +" + axis[a] + " at " + pf.ToString("F2") : "")
                            + (f && k ? ", " : "") + (k ? "what faces -" + axis[a] + " at " + pb.ToString("F2") : "");
                    air &= ok;
                }
                return air;
            }

            // Coverage by the surfaces that face a ray going along d (the shader's Facing . |d|), w = which axes count.
            private static float Cover(uint u, Vector3 d, Vector3 w)
            {
                return (((d.x > 0f ? u >> 15 : u) & 31) * Mathf.Abs(d.x) * w.x + ((d.y > 0f ? u >> 20 : u >> 5) & 31) * Mathf.Abs(d.y) * w.y
                    + ((d.z > 0f ? u >> 25 : u >> 10) & 31) * Mathf.Abs(d.z) * w.z) / 31f;
            }

            // The shader's March / ThroughFrom along d: what is left of the ray; stop = how far out it was stopped
            // (-1 = not). masked: in the 1 m or 2 m voxel it starts in, the faces behind its start are not met
            // (March always, Through for a cell's centre).
            public float Go(Vector3 ro, Vector3 d, float far, int steps, float floor, bool masked, out float stop)
            {
                float run = 0f, trans = 1f, t = 0f;
                stop = -1f;
                Vector3 ahead = Vector3.one;
                Vector3Int v0 = default;
                uint u0 = At(ro, out _, out int l0, out Vector3 q0);
                masked = masked && l0 >= 0 && pos[l0] != null;
                if (masked)
                {
                    v0 = Fl3(q0);
                    InAir(l0, u0, q0, d, out ahead, out _);
                }
                for (int i = 0; i < steps && t < far && trans >= floor; i++)
                {
                    uint u = At(ro + d * t, out float vs, out int l, out Vector3 q);
                    if (u == uint.MaxValue) break;
                    float o = Cover(u, d, masked && l == l0 && Fl3(q) == v0 ? ahead : Vector3.one);
                    if (o > 0f)
                    {
                        run += 0.7f * o;
                        if (run >= 0.5f) { stop = t; return 0f; }
                    }
                    else { trans *= 1f - run; run = 0f; }
                    t += vs * 0.7f;
                }
                return trans * (1f - run);
            }

            // "Something overhead": Through, 64 m straight up. cell = from a cell's centre (STATE), else from a point off a surface (KindAt).
            public float Up(Vector3 q, bool cell, out float stop) => Go(q, Vector3.up, 64f, 192, 0.02f, cell, out stop);

            // How much sky a cell centred here sees: 256 rays as a cell shoots them (CellRay's March). Every ray
            // that gets out through the voxels is then sent through the game's own collision: nothing in its
            // way, or only glass, is an opening; a wall, a floor or a roof in its way is geometry the voxels
            // let it through - a leak - and is named, with how far out and which way.
            public string Sky(Vector3 ro)
            {
                const int Rays = 256;
                var got = new float[3];
                var of = new int[3];
                var blocked = new Dictionary<string, float[]>();     // collider -> rays, nearest hit, sum of the way they go
                float open = 0f, shut = 0f;
                for (int i = 0; i < Rays; i++)
                {
                    float phi = 6.2831853f * Mathf.Repeat(i * 0.6180339887f, 1f), cz = 1f - (2f * i + 1f) / Rays, sz = Mathf.Sqrt(Mathf.Clamp01(1f - cz * cz));
                    var d = new Vector3(Mathf.Cos(phi) * sz, cz, Mathf.Sin(phi) * sz);
                    int band = d.y > 0.33f ? 0 : d.y < -0.33f ? 2 : 1;
                    of[band]++;
                    float left = Go(ro, d, float.MaxValue, 300, 0.03f, true, out _);
                    got[band] += left;
                    if (left < 0.05f) continue;
                    if (!Physics.Raycast(ro, d, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore)) { open += left; continue; }
                    shut += left;
                    string name = hit.collider.name + " (layer " + LayerMask.LayerToName(hit.collider.gameObject.layer) + ")";
                    if (!blocked.TryGetValue(name, out float[] b)) blocked[name] = b = new float[] { 0f, float.MaxValue, 0f, 0f, 0f };
                    b[0] += left;
                    b[1] = Mathf.Min(b[1], hit.distance);
                    b[2] += d.x; b[3] += d.y; b[4] += d.z;
                }
                float all = (got[0] + got[1] + got[2]) / Rays;
                var sb = new System.Text.StringBuilder();
                sb.Append("Of 256 rays from its centre ").Append((100f * all).ToString("F0")).Append("% reach the sky through the voxels (")
                  .Append((100f * got[0] / of[0]).ToString("F0")).Append("% of those going up, ").Append((100f * got[1] / of[1]).ToString("F0"))
                  .Append("% of those going level, ").Append((100f * got[2] / of[2]).ToString("F0")).Append("% of those going down).");
                if (open + shut < 0.05f) return sb.ToString();
                sb.Append(" Those that get out, against the game's collision: ").Append(open.ToString("F1")).Append(" rays' worth with nothing in the way, ")
                  .Append(shut.ToString("F1")).Append(" with something (glass = a window; a wall, floor or roof = a LEAK):");
                var names = new List<string>(blocked.Keys);
                names.Sort((x, y) => blocked[y][0].CompareTo(blocked[x][0]));
                for (int k = 0; k < names.Count && k < 8; k++)
                {
                    float[] b = blocked[names[k]];
                    var way = new Vector3(b[2], b[3], b[4]).normalized;
                    sb.Append("\n      ").Append(b[0].ToString("F1")).Append(" x '").Append(names[k]).Append("' from ").Append(b[1].ToString("F1"))
                      .Append(" m out, going ").Append(way.ToString("F2"));
                }
                if (names.Count > 8) sb.Append("\n      ... and ").Append(names.Count - 8).Append(" more");
                return sb.ToString();
            }

            // Why a cell centred here is dead (the shader's Dead): in the 1 m and 2 m voxels its centre is not in
            // the open air; in the others a surface covers half its voxel or more along some axis.
            public string WhyDead(Vector3 centre)
            {
                uint u = At(centre, out float vs, out int level, out Vector3 q);
                if (u == uint.MaxValue) return "dead (outside the voxels)";
                if (pos[level] != null)
                    return InAir(level, u, q, Vector3.zero, out _, out string why) ? "dead (yet in the air by the " + vs + " m voxels as they are now)" : "dead (" + vs + " m voxel: " + why + ")";
                string[] axis = { "+x", "+y", "+z", "-x", "-y", "-z" };
                int most = 0, which = 0;
                for (int k = 0; k < 6; k++)
                {
                    int v = (int)((u >> (5 * k)) & 31);
                    if (v > most) { most = v; which = k; }
                }
                return "dead (" + vs + " m voxel, " + (most >= 16 ? axis[which] + " " + most : "open now: at most " + most) + ")";
            }
        }

        private static Vector3Int Fl3(Vector3 v) => new Vector3Int(Mathf.FloorToInt(v.x), Mathf.FloorToInt(v.y), Mathf.FloorToInt(v.z));

        // Stored linear light -> the game's kind of number, x1000.
        private static string Show(Unity.Collections.NativeArray<ushort> px, int i)
        {
            float lin = (Mathf.HalfToFloat(px[i]) + Mathf.HalfToFloat(px[i + 1]) + Mathf.HalfToFloat(px[i + 2])) / (3f * Store);
            return Mathf.RoundToInt(Mathf.Pow(Mathf.Max(lin, 0f), 1f / 2.2f) * 1000f).ToString();
        }

        private static string VoxText(uint u)
        {
            if (u == uint.MaxValue) return "outside the voxel world";
            if (u == 0) return "empty";
            return "facing +x/+y/+z " + (u & 31) + "/" + ((u >> 5) & 31) + "/" + ((u >> 10) & 31) + ", -x/-y/-z " + ((u >> 15) & 31) + "/" + ((u >> 20) & 31) + "/" + ((u >> 25) & 31) + " (of 31)";
        }

        // The shader's IndoorAt.
        public static bool IndoorCpu(Vector3 p)
        {
            for (int k = 0; k < _indoorCount; k++)
            {
                Vector3 q = _indoor[k].MultiplyPoint3x4(p);
                if (Mathf.Abs(q.x) <= 1f && Mathf.Abs(q.y) <= 1f && Mathf.Abs(q.z) <= 1f) return true;
            }
            return false;
        }

        //--- "the lookup under me" ---------------------------------------------------------------------------

        // Debug: redoes on the CPU, from the textures the GPU reads, what the fine volume's lookup does for the
        // floor under the camera (TraceSixProbes.shader Level + OffTheFaces, line for line): every point it tries,
        // the voxel there, and why a step is or is not taken. Maps of the cells around show how thick the dead
        // cells are. If this says a pixel finds a cell and the screen says it does not, the fault is on the GPU.
        public static void Under(Camera cam)
        {
            Grid g0 = _g[0];
            if (g0.amb == null || g0.sweeps < 1) { Plugin.MyLog.LogInfo("[TraceSix] Under me: the fine light volume is not filled yet."); return; }
            Vector3 eye = cam.transform.position;
            Vector3Int min = g0.min;
            RenderTexture front = g0.amb;   // both reads in this frame: the two textures swap roles when a redo completes
            bool[] open = null;
            float[] up = null;
            int pending = 2;
            bool failed = false;
            System.Action next = () =>
            {
                if (--pending > 0) return;
                if (failed) { Plugin.MyLog.LogWarning("[TraceSix] Under me: readback of the light volume failed."); return; }
                VoxelScene.ReadFine((org, vox, faces) =>
                {
                    if (vox == null || faces == null) { Plugin.MyLog.LogWarning("[TraceSix] Under me: readback of the voxels failed."); return; }
                    try { Plugin.MyLog.LogInfo(new Lookup(eye, min, open, up, org, vox, faces).Report()); }
                    catch (System.Exception e) { Plugin.MyLog.LogWarning("[TraceSix] Under me failed: " + e); }
                });
            };
            AsyncGPUReadback.Request(front, 0, 0, NX, 0, NY, 0, NZ, TextureFormat.RGBAHalf, req =>   // the +x block: its alpha
            {
                if (req.hasError) failed = true;
                else
                {
                    open = new bool[NX * NY * NZ];
                    for (int z = 0; z < NZ; z++)
                    {
                        var px = req.GetData<ushort>(z);
                        for (int i = 0; i < NX * NY; i++) open[z * NY * NX + i] = Mathf.HalfToFloat(px[i * 4 + 3]) > 0.25f;
                    }
                }
                next();
            });
            AsyncGPUReadback.Request(front, 0, 0, NX, 0, NY, NZ * 2, NZ, TextureFormat.RGBAHalf, req =>   // the +y block: light on upward surfaces
            {
                if (req.hasError) failed = true;
                else
                {
                    up = new float[NX * NY * NZ];
                    for (int z = 0; z < NZ; z++)
                    {
                        var px = req.GetData<ushort>(z);
                        for (int i = 0; i < NX * NY; i++)
                        {
                            float lin = (Mathf.HalfToFloat(px[i * 4]) + Mathf.HalfToFloat(px[i * 4 + 1]) + Mathf.HalfToFloat(px[i * 4 + 2])) / (3f * Store);
                            up[z * NY * NX + i] = Mathf.Pow(Mathf.Max(lin, 0f), 1f / 2.2f);   // the game's kind of number
                        }
                    }
                }
                next();
            });
        }

        private sealed class Lookup
        {
            private const float S = 0.5f, Face = 4f / 31f, Ahead = 0.125f, Stop = 0.5f;   // the shader's
            private readonly Vector3 _eye, _org;
            private readonly Vector3Int _min;
            private readonly bool[] _open;
            private readonly float[] _up;           // light on upward surfaces per cell, as the game counts it
            private readonly System.Func<Vector3Int, uint> _vox, _faces;
            private System.Text.StringBuilder _t;   // the trace, while one is wanted

            public Lookup(Vector3 eye, Vector3Int min, bool[] open, float[] up, Vector3 org, System.Func<Vector3Int, uint> vox, System.Func<Vector3Int, uint> faces)
            {
                _eye = eye; _min = min; _open = open; _up = up; _org = org; _vox = vox; _faces = faces;
            }

            private int Tx(Vector3Int c)   // the texel of a window-relative cell (the shader's Wrap)
            {
                Vector3Int t = c + _min;
                t = new Vector3Int(((t.x % NX) + NX) % NX, ((t.y % NY) + NY) % NY, ((t.z % NZ) + NZ) % NZ);
                return (t.z * NY + t.y) * NX + t.x;
            }
            private bool Open(Vector3Int c) => c.x >= 0 && c.y >= 0 && c.z >= 0 && c.x < NX && c.y < NY && c.z < NZ && _open[Tx(c)];
            private static Vector3Int Fl(Vector3 v) => new Vector3Int(Mathf.FloorToInt(v.x), Mathf.FloorToInt(v.y), Mathf.FloorToInt(v.z));
            private static void Six(uint u, out Vector3 fwd, out Vector3 bwd)
            {
                if (u == uint.MaxValue) u = 0;
                fwd = new Vector3(u & 31, (u >> 5) & 31, (u >> 10) & 31) / 31f;
                bwd = new Vector3((u >> 15) & 31, (u >> 20) & 31, (u >> 25) & 31) / 31f;
            }
            private static string N31(Vector3 v) => Mathf.RoundToInt(v.x * 31f) + "/" + Mathf.RoundToInt(v.y * 31f) + "/" + Mathf.RoundToInt(v.z * 31f);
            private static string F2(Vector3 v) => v.x.ToString("F2") + "/" + v.y.ToString("F2") + "/" + v.z.ToString("F2");
            private static string I3(Vector3Int v) => "(" + v.x + "," + v.y + "," + v.z + ")";
            private string Voxel(Vector3Int vq)
            {
                Six(_vox(vq), out Vector3 fwd, out Vector3 bwd);
                Six(_faces(vq), out Vector3 pf, out Vector3 pb);
                return "faces +x/+y/+z " + N31(fwd) + " at " + F2(pf) + ", -x/-y/-z " + N31(bwd) + " at " + F2(pb);
            }

            // 'G' = a cell on the search, '1' = stepped off a face, '2' = across an open axis, 'x' = no cell.
            private char Pixel(Vector3 p, Vector3 n)
            {
                Vector3 to = _eye - p;
                float reach = to.magnitude;
                Vector3 toEye = to / Mathf.Max(reach, 1e-4f), m = (n + toEye).normalized;
                for (int k = 1; k <= 16; k++)
                {
                    float along = 0.5f * S * (k - 4);
                    Vector3 pt = k <= 4 ? p + m * (0.5f * S * k) : p + toEye * Mathf.Min(along, reach);
                    Vector3Int hc = Fl(pt / S - (Vector3)_min);
                    bool o = Open(hc);
                    _t?.Append("\n      search ").Append(k).Append(k <= 4 ? " (off the surface) " : " (line of sight)  ").Append((pt.y - p.y).ToString("F2"))
                        .Append(" m up: cell ").Append(I3(hc)).Append(o ? " OPEN" : " dead");
                    if (o) return 'G';
                    if (along >= reach) break;
                }
                return Off(p, toEye, reach);
            }

            private char Off(Vector3 p, Vector3 toEye, float reach)
            {
                for (int k = 1; k <= 12; k++)
                {
                    float along = Mathf.Min(0.5f * S * k, reach);
                    Vector3 pt = p + toEye * along, g = pt / S - (Vector3)_min, q = (pt - _org) / S;
                    if (g.x < 4f || g.y < 4f || g.z < 4f || g.x >= NX - 4f || g.y >= NY - 4f || g.z >= NZ - 4f
                        || q.x < 4f || q.y < 4f || q.z < 4f || q.x >= VoxelScene.Res - 4f || q.y >= VoxelScene.Res - 4f || q.z >= VoxelScene.Res - 4f)
                    {
                        _t?.Append("\n      step ").Append(k).Append(": too near the edge of a volume (cell ").Append(F2(g)).Append(", voxel ").Append(F2(q)).Append("): gives up");
                        return 'x';
                    }
                    Vector3Int hc = Fl(g), vq = Fl(q);
                    Vector3 x = q - (Vector3)vq;
                    Six(_vox(vq), out Vector3 fwd, out Vector3 bwd);
                    Six(_faces(vq), out Vector3 pf, out Vector3 pb);
                    Vector3 fp = Vector3.zero, fm = Vector3.zero, mag = Vector3.zero;
                    Vector3Int o = Vector3Int.zero;
                    for (int a = 0; a < 3; a++)
                    {
                        fp[a] = x[a] > pf[a] + Ahead ? fwd[a] : 0f;
                        fm[a] = x[a] < pb[a] - Ahead ? bwd[a] : 0f;
                        if (Mathf.Max(fp[a], fm[a]) >= Face && Mathf.Min(fp[a], fm[a]) < Stop) o[a] = fp[a] > fm[a] ? 1 : (fp[a] < fm[a] ? -1 : 0);
                        mag[a] = Mathf.Abs(fp[a] - fm[a]) * Mathf.Abs(o[a]);
                    }
                    _t?.Append("\n      step ").Append(k).Append(", ").Append((pt.y - p.y).ToString("F2")).Append(" m up: cell ").Append(I3(hc)).Append(Open(hc) ? " OPEN" : " dead")
                        .Append(", voxel ").Append(I3(vq)).Append(' ').Append(Voxel(vq)).Append("; the point is at ").Append(F2(x))
                        .Append("; in front of +").Append(N31(fp)).Append(" -").Append(N31(fm)).Append(" -> step ").Append(I3(o));
                    int most = mag.x >= mag.y && mag.x >= mag.z ? 0 : (mag.y >= mag.z ? 1 : 2);
                    for (int t = 0; t < 5; t++)
                    {
                        Vector3Int oo = o;
                        if (t >= 1) { int keep = t == 1 ? most : t - 2; oo = Vector3Int.zero; oo[keep] = o[keep]; }
                        if (oo == Vector3Int.zero) continue;
                        bool ok = Open(hc + oo);
                        _t?.Append("; ").Append(I3(oo)).Append(ok ? " OPEN" : " dead");
                        if (ok) return '1';
                    }
                    for (int a = 0; a < 3; a++)
                    {
                        Vector3Int e = Vector3Int.zero;
                        e[a] = 1;
                        int lo = 0, hi = 0;
                        for (int r = 1; r <= 3; r++)
                        {
                            Six(_vox(vq - e * (r - 1)), out Vector3 f2, out Vector3 b2);
                            if (Mathf.Max(f2[a], b2[a]) >= Face) break;
                            if (Open(hc - e * r)) { lo = r; break; }
                        }
                        for (int r = 1; r <= 3; r++)
                        {
                            Six(_vox(vq + e * (r - 1)), out Vector3 f2, out Vector3 b2);
                            if (Mathf.Max(f2[a], b2[a]) >= Face) break;
                            if (Open(hc + e * r)) { hi = r; break; }
                        }
                        _t?.Append("; axis ").Append("xyz"[a]).Append(Mathf.Max(fwd[a], bwd[a]) >= Face ? " has faces" : " open: cells at -" + lo + " / +" + hi);
                        if (lo + hi > 0) return '2';
                    }
                    if (along >= reach) break;
                }
                return 'x';
            }

            public string Report()
            {
                var sb = new System.Text.StringBuilder();
                Vector3Int ec = Fl(_eye / S - (Vector3)_min), ev = Fl((_eye - _org) / S);
                Vector3 off = _org / S;
                // The floor: the first voxel under the eye that an upward-facing surface covers half of.
                float floorY = _eye.y - 1.6f;
                bool seen = false;
                for (float h = 0.2f; h <= 3f && !seen; h += 0.05f)
                {
                    Vector3Int vq = Fl((_eye + Vector3.down * h - _org) / S);
                    Six(_vox(vq), out Vector3 fwd, out Vector3 bwd);
                    Six(_faces(vq), out Vector3 pf, out Vector3 pb);
                    if (fwd.y >= 0.5f) { floorY = _org.y + (vq.y + pf.y) * S; seen = true; }
                }
                Vector3 foot = new Vector3(_eye.x, floorY + 0.005f, _eye.z);
                sb.Append("[TraceSix] Under me: eye at ").Append(_eye.ToString("F2")).Append(", cell ").Append(I3(ec)).Append(Open(ec) ? " OPEN" : " DEAD")
                  .Append(" of the fine light volume (lowest cell ").Append(I3(_min)).Append("), voxel ").Append(I3(ev)).Append(' ').Append(Voxel(ev))
                  .Append(". Voxel grid against the cell grid: off by ").Append(F2(new Vector3(off.x - Mathf.Round(off.x), off.y - Mathf.Round(off.y), off.z - Mathf.Round(off.z))))
                  .Append(" (should be 0). Floor ").Append(seen ? "found in the voxels " : "NOT found in the voxels, assumed ").Append((_eye.y - floorY).ToString("F2"))
                  .Append(" m under the eye. Marked door / window openings holding the eye: ").Append(VoxelScene.HolesAt(_eye)).Append(", the floor point: ").Append(VoxelScene.HolesAt(foot)).Append('.');

                // What each floor pixel around gets (x to the right, z up the page; 12.5 cm apart; the middle is under the eye).
                sb.Append("\n  The floor around, 1.5 m square - G = a cell on the plain search, 1 = stepped off a face, 2 = across an open axis, x = NO CELL:");
                var fails = new System.Collections.Generic.List<Vector3>();
                for (int iz = 6; iz >= -6; iz--)
                {
                    sb.Append("\n    ");
                    for (int ix = -6; ix <= 6; ix++)
                    {
                        Vector3 p = foot + new Vector3(ix * 0.125f, 0f, iz * 0.125f);
                        char r = Pixel(p, Vector3.up);
                        if (r == 'x') fails.Add(p);
                        sb.Append(ix == 0 && iz == 0 ? char.ToUpper(r) : r).Append(' ');
                    }
                }
                // The cells, level by level (# dead, . open; the eye's column in brackets).
                foreach (float up in new[] { 0.25f, 0.75f, 1.25f, _eye.y - floorY })
                {
                    int cy = Mathf.FloorToInt((floorY + up) / S) - _min.y;
                    sb.Append("\n  Cells ").Append(up.ToString("F2")).Append(" m above the floor (layer ").Append(cy).Append("), 11 x 11 around the eye:");
                    for (int dz = 5; dz >= -5; dz--)
                    {
                        sb.Append("\n    ");
                        for (int dx = -5; dx <= 5; dx++)
                        {
                            bool o = Open(new Vector3Int(ec.x + dx, cy, ec.z + dz));
                            sb.Append(dx == 0 && dz == 0 ? (o ? "[.]" : "[#]") : (o ? " . " : " # "));
                        }
                    }
                }
                // How much light the cells hold: is the dark side unlit, or only much less lit?
                Vector3 sky = SkyAmbientUp();
                foreach (float up in new[] { 0.75f, 1.25f })
                {
                    int cy = Mathf.FloorToInt((floorY + up) / S) - _min.y;
                    sb.Append("\n  Light the cells hold for upward-facing surfaces, x1000, ").Append(up.ToString("F2"))
                      .Append(" m above the floor (## = dead; the middle is the eye's column). Open sky gives ")
                      .Append(Mathf.RoundToInt((sky.x + sky.y + sky.z) / 3f * 1000f)).Append(':');
                    for (int dz = 5; dz >= -5; dz--)
                    {
                        sb.Append("\n    ");
                        for (int dx = -5; dx <= 5; dx++)
                        {
                            var c = new Vector3Int(ec.x + dx, cy, ec.z + dz);
                            sb.Append(Open(c) ? Mathf.RoundToInt(_up[Tx(c)] * 1000f).ToString().PadLeft(5) : "   ##");
                        }
                    }
                }
                // What the dead cells next to the eye hold, 0.75 m above the floor.
                sb.Append("\n  Dead cells within one cell of the eye's column, 0.75 m above the floor (offset in cells x,z):");
                int vy = Mathf.FloorToInt((floorY + 0.75f - _org.y) / S), ly = Mathf.FloorToInt((floorY + 0.75f) / S) - _min.y;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (!Open(new Vector3Int(ec.x + dx, ly, ec.z + dz)))
                            sb.Append("\n    (").Append(dx).Append(',').Append(dz).Append(") ").Append(Voxel(new Vector3Int(ev.x + dx, vy, ev.z + dz)));

                // Step by step: the pixel under the eye, and the two failing pixels nearest to it.
                fails.Sort((a, b) => (a - foot).sqrMagnitude.CompareTo((b - foot).sqrMagnitude));
                var traced = new System.Collections.Generic.List<Vector3> { foot };
                foreach (Vector3 f in fails) if (traced.Count < 3 && (f - foot).sqrMagnitude > 1e-6f) traced.Add(f);
                foreach (Vector3 p in traced)
                {
                    _t = new System.Text.StringBuilder();
                    char r = Pixel(p, Vector3.up);
                    sb.Append("\n  Floor pixel at ").Append((p - foot).ToString("F2")).Append(" from under the eye -> ").Append(r).Append(':').Append(_t);
                    _t = null;
                }
                return sb.ToString();
            }
        }

        //--- the game's interiors ----------------------------------------------------------------------------

        // The game marks its interiors with hand-placed volumes (StencilShadow: a mesh per building or room) and
        // draws them into a per-pixel mask every frame. The cells of the light volumes are marked with the same
        // volumes (as the box around each mesh), so a pixel indoors can be given indoor cells where cells are
        // too coarse to tell rooms apart (the 2 m and 8 m volumes). All of them, also the ones the game has
        // switched off for now: one search while the raid loads.
        private const int MaxIndoor = 256;     // the shader's array
        private static readonly Matrix4x4[] _indoor = new Matrix4x4[MaxIndoor];
        private static int _indoorCount;
        // Every interior volume of the map. Should a map have more than the shader takes, the MaxIndoor nearest
        // the camera are in use, picked again once it has moved 8 m (Streets, its merged meshes taken room by
        // room, has about 490: a volume that drops out turns its rooms into "outdoors" until it comes back in).
        private static readonly List<Matrix4x4> _indoorAll = new List<Matrix4x4>();
        private static readonly List<Vector4> _indoorBall = new List<Vector4>();   // centre, radius
        private static float[] _indoorKey = new float[0];
        private static int[] _indoorIdx = new int[0];
        private static Vector3 _indoorPickedAt;
        private static bool[] _indoorUsed = new bool[0];
        private static int _indoorPicks;

        // The cost test: the shader asked no interior volume at all (every lookup beyond the 0.5 m cells asks
        // each of them in turn), or all of them again.
        public static void AskIndoor(bool on) => Shader.SetGlobalFloat("_GiIndoorCount", on ? _indoorCount : 0);
        public static int IndoorCount => _indoorCount;

        private static void PickIndoor(Vector3 cam)
        {
            int n = _indoorAll.Count;
            if (n <= MaxIndoor || (cam - _indoorPickedAt).sqrMagnitude < 64f) return;
            _indoorPickedAt = cam;
            if (_indoorKey.Length != n) { _indoorKey = new float[n]; _indoorIdx = new int[n]; }
            for (int i = 0; i < n; i++)
            {
                Vector4 b = _indoorBall[i];
                _indoorKey[i] = (new Vector3(b.x, b.y, b.z) - cam).magnitude - b.w;
                _indoorIdx[i] = i;
            }
            System.Array.Sort(_indoorKey, _indoorIdx);
            // How many of the volumes in use are new ones: a volume that drops out turns its rooms into
            // "outdoors" for cells and pixels alike, one that comes in turns them back. Logged (30 lines a raid)
            // to see whether that happens in view - Interchange has 195 volumes for these 96 places.
            int swapped = 0;
            if (_indoorUsed.Length != n) _indoorUsed = new bool[n];
            for (int i = 0; i < MaxIndoor; i++) if (!_indoorUsed[_indoorIdx[i]]) swapped++;
            for (int i = 0; i < n; i++) _indoorUsed[i] = false;
            for (int i = 0; i < MaxIndoor; i++) _indoorUsed[_indoorIdx[i]] = true;
            if (swapped > 0 && _indoorPicks++ > 0 && _indoorPicks <= 31)
                Plugin.MyLog.LogInfo("[TraceSix] Interior volumes in use: " + swapped + " of " + MaxIndoor + " swapped for nearer ones; the farthest in use now begins "
                    + Mathf.Max(0f, _indoorKey[MaxIndoor - 1]).ToString("F0") + " m from the camera, the nearest left out " + Mathf.Max(0f, _indoorKey[MaxIndoor]).ToString("F0") + " m.");
            if (swapped == 0) return;                                        // the same volumes as before: nothing to redo
            for (int i = 0; i < MaxIndoor; i++) _indoor[i] = _indoorAll[_indoorIdx[i]];
            _indoorCount = MaxIndoor;
            Shader.SetGlobalMatrixArray("_GiIndoor", _indoor);
            Shader.SetGlobalFloat("_GiIndoorCount", _indoorCount);
            IndoorIndex(false);
        }

        // Which interior volumes reach where (TraceSixProbes: IndoorAt). A grid of GridN x GridN squares over the
        // ground the volumes in use cover; per square the volumes whose bounding box (a hand's width wider)
        // touches it, as a run in one list. A point inside a volume is inside that volume's bounds, so the
        // volume is in its square's list: the shader asks only those and gets what asking all of them gives.
        // Checked here on the CPU when the raid loads, on points inside every volume and all over the grid;
        // one differing answer and the grid is left out (the shader then asks every volume, as before 0.24.27).
        private const int GridN = 128, ListW = 256, ListMaxRows = 2048;
        private static Texture2D _indoorGrid, _indoorList;
        private static Vector4 _gridA, _gridB;
        private static float[] _gridData, _listData;
        private static readonly int IdGridTex = Shader.PropertyToID("_GiIndoorGrid"), IdListTex = Shader.PropertyToID("_GiIndoorList"),
            IdGridA = Shader.PropertyToID("_GiIndoorGridA"), IdGridB = Shader.PropertyToID("_GiIndoorGridB");

        private static void PublishIndoorIndex()
        {
            Shader.SetGlobalVector(IdGridA, _gridA);
            Shader.SetGlobalVector(IdGridB, _gridB);
            if (_indoorGrid != null) Shader.SetGlobalTexture(IdGridTex, _indoorGrid);
            if (_indoorList != null) Shader.SetGlobalTexture(IdListTex, _indoorList);
        }

        // The shader's IndoorAt through the grid, on the arrays the textures are filled from.
        private static bool IndoorByGrid(Vector3 p)
        {
            float gx = (p.x - _gridA.x) * _gridA.z, gz = (p.z - _gridA.y) * _gridA.w;
            if (gx < 0f || gz < 0f || gx >= GridN || gz >= GridN) return false;
            int at = ((int)gz * GridN + (int)gx) * 2;
            int first = (int)_gridData[at], n = (int)_gridData[at + 1];
            for (int j = 0; j < n; j++)
            {
                Vector3 q = _indoor[(int)_listData[first + j]].MultiplyPoint3x4(p);
                if (Mathf.Abs(q.x) <= 1f && Mathf.Abs(q.y) <= 1f && Mathf.Abs(q.z) <= 1f) return true;
            }
            return false;
        }

        private static void IndoorIndex(bool log)
        {
            _gridA = _gridB = Vector4.zero;
            if (_indoorCount > 0)
            {
                try { BuildIndoorIndex(log); }
                catch (System.Exception e)
                {
                    _gridA = _gridB = Vector4.zero;
                    Plugin.MyLog.LogWarning("[TraceSix] Interior volumes: the grid could not be built (" + e.GetType().Name + ": " + e.Message + "); every volume is asked.");
                }
            }
            PublishIndoorIndex();
        }

        private static void BuildIndoorIndex(bool log)
        {
            const float Pad = 0.05f;
            int n = _indoorCount;
            var lo = new Vector3[n];
            var hi = new Vector3[n];
            var fwd = new Matrix4x4[n];
            Vector3 mn = Vector3.one * 1e9f, mx = Vector3.one * -1e9f;
            for (int k = 0; k < n; k++)
            {
                Matrix4x4 m = fwd[k] = _indoor[k].inverse;               // the +-1 box -> world
                Vector3 c = m.MultiplyPoint3x4(Vector3.zero);
                var h = new Vector3(Mathf.Abs(m.m00) + Mathf.Abs(m.m01) + Mathf.Abs(m.m02) + Pad,
                                    Mathf.Abs(m.m10) + Mathf.Abs(m.m11) + Mathf.Abs(m.m12) + Pad,
                                    Mathf.Abs(m.m20) + Mathf.Abs(m.m21) + Mathf.Abs(m.m22) + Pad);
                lo[k] = c - h;
                hi[k] = c + h;
                mn = Vector3.Min(mn, lo[k]);
                mx = Vector3.Max(mx, hi[k]);
            }
            float sx = GridN / Mathf.Max(mx.x - mn.x, 1f), sz = GridN / Mathf.Max(mx.z - mn.z, 1f);
            var cells = new List<int>[GridN * GridN];
            int total = 0, most = 0, empty = GridN * GridN;
            for (int k = 0; k < n; k++)
            {
                int x0 = Mathf.Clamp((int)((lo[k].x - mn.x) * sx), 0, GridN - 1), x1 = Mathf.Clamp((int)((hi[k].x - mn.x) * sx), 0, GridN - 1);
                int z0 = Mathf.Clamp((int)((lo[k].z - mn.z) * sz), 0, GridN - 1), z1 = Mathf.Clamp((int)((hi[k].z - mn.z) * sz), 0, GridN - 1);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        List<int> l = cells[z * GridN + x];
                        if (l == null) { l = cells[z * GridN + x] = new List<int>(4); empty--; }
                        l.Add(k);
                        total++;
                        most = Mathf.Max(most, l.Count);
                    }
            }
            int rows = Mathf.Max(1, (total + ListW - 1) / ListW);
            if (rows > ListMaxRows)
            {
                Plugin.MyLog.LogInfo("[TraceSix] Interior volumes: no grid (" + total + " entries are more than it holds); every volume is asked.");
                return;
            }
            _gridData = new float[GridN * GridN * 2];
            _listData = new float[ListW * rows];
            int at = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                _gridData[i * 2] = at;
                if (cells[i] == null) continue;
                _gridData[i * 2 + 1] = cells[i].Count;
                foreach (int k in cells[i]) _listData[at++] = k;
            }
            _gridA = new Vector4(mn.x, mn.z, sx, sz);
            _gridB = new Vector4(GridN, 0f, 0f, 0f);

            // the check: the grid's answer against asking every volume (at raid load only: a later re-pick of
            // the nearest volumes builds the same way and must not hold a frame up)
            var rnd = new System.Random(12345);
            int tested = 0, differ = 0;
            for (int k = 0; log && k < n; k++)
                for (int s = 0; s < 48; s++)
                {
                    // inside the volume, on its faces and just outside it
                    float grow = s < 32 ? 1f : (s < 40 ? 0.9999f : 1.02f);
                    var q = new Vector3((float)(rnd.NextDouble() * 2.0 - 1.0), (float)(rnd.NextDouble() * 2.0 - 1.0), (float)(rnd.NextDouble() * 2.0 - 1.0));
                    if (s >= 32) q[s % 3] = (s & 4) != 0 ? 1f : -1f;
                    Vector3 p = fwd[k].MultiplyPoint3x4(q * grow);
                    tested++;
                    if (IndoorByGrid(p) != IndoorCpu(p)) differ++;
                }
            for (int s = 0; log && s < 8192; s++)
            {
                var p = new Vector3(Mathf.Lerp(mn.x - 5f, mx.x + 5f, (float)rnd.NextDouble()), Mathf.Lerp(mn.y - 5f, mx.y + 5f, (float)rnd.NextDouble()),
                                    Mathf.Lerp(mn.z - 5f, mx.z + 5f, (float)rnd.NextDouble()));
                tested++;
                if (IndoorByGrid(p) != IndoorCpu(p)) differ++;
            }
            if (differ > 0)
            {
                _gridA = _gridB = Vector4.zero;
                Plugin.MyLog.LogWarning("[TraceSix] Interior volumes: the grid answered " + differ + " of " + tested + " test points differently from asking every volume - it is left out, every volume is asked.");
                return;
            }
            if (_indoorGrid == null)
                _indoorGrid = new Texture2D(GridN, GridN, TextureFormat.RGFloat, false, true) { name = "TraceSix Interior Grid", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            if (_indoorList == null || _indoorList.height != rows)
            {
                if (_indoorList != null) Object.Destroy(_indoorList);
                _indoorList = new Texture2D(ListW, rows, TextureFormat.RFloat, false, true) { name = "TraceSix Interior Lists", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            }
            _indoorGrid.SetPixelData(_gridData, 0);
            _indoorGrid.Apply(false, false);
            _indoorList.SetPixelData(_listData, 0);
            _indoorList.Apply(false, false);
            if (log)
                Plugin.MyLog.LogInfo("[TraceSix] Interior volumes: a grid of " + GridN + " x " + GridN + " squares (" + (1f / sx).ToString("F1") + " x " + (1f / sz).ToString("F1")
                    + " m) says which reach where - " + total + " entries, at most " + most + " volumes in one square, " + (100 * empty / (GridN * GridN)) + "% of squares with none; "
                    + tested + " test points answered as by asking all " + n + ".");
        }

        // A StencilShadow mesh can be many rooms in one object. Customs has one of 115 rooms whose bounding box
        // is the whole map (978 x 34 x 465 m; the rooms fill 1.7 % of it): with "the box around each mesh" all of
        // Customs counted as inside an interior volume, every lookup beyond the 0.5 m cells marched upward for a
        // roof (0.30 ms of Air Cells' picture, measured outdoors) and "indoors" meant "under anything".
        // Such a mesh is taken room by room: its vertices joined where they coincide, the pieces its triangles
        // connect, and round each piece the smallest upright box, turned about the world's up (whole degrees,
        // then tenths). Tools/gisix/roomscan.py does the same on the game's files, map by map.
        // The game keeps these meshes on the GPU only (not readable), so they are read back from there. Anything
        // unexpected - no buffer, another vertex format, positions that do not fit the mesh's bounds - and the
        // mesh counts as one box, as before 0.24.30; so does a mesh of one piece, one with a loose sheet among
        // its pieces, or one whose pieces fill its box anyway.
        private static bool ReadMesh(Mesh mesh, out Vector3[] v, out int[] tri, out string why)
        {
            v = null;
            tri = null;
            why = null;
            if (mesh.isReadable)
            {
                v = mesh.vertices;
                tri = mesh.triangles;
                return true;
            }
            if (!mesh.HasVertexAttribute(VertexAttribute.Position)) { why = "it has no positions"; return false; }
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position), off = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            VertexAttributeFormat fmt = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
            bool half = fmt == VertexAttributeFormat.Float16;
            if ((fmt != VertexAttributeFormat.Float32 && !half) || mesh.GetVertexAttributeDimension(VertexAttribute.Position) < 3) { why = "its positions are kept as " + fmt; return false; }
            int nv = mesh.vertexCount, stride;
            byte[] raw;
            GraphicsBuffer vb = mesh.GetVertexBuffer(stream);
            if (vb == null) { why = "it has no vertex buffer on the GPU"; return false; }
            try
            {
                stride = vb.stride;
                raw = new byte[vb.count * stride];
                vb.GetData(raw);
            }
            finally { vb.Dispose(); }
            if (stride < off + (half ? 6 : 12) || raw.Length < nv * stride) { why = "its vertex buffer is smaller than the mesh says"; return false; }
            v = new Vector3[nv];
            for (int i = 0; i < nv; i++)
            {
                int at = i * stride + off;
                v[i] = half
                    ? new Vector3(Mathf.HalfToFloat(System.BitConverter.ToUInt16(raw, at)), Mathf.HalfToFloat(System.BitConverter.ToUInt16(raw, at + 2)), Mathf.HalfToFloat(System.BitConverter.ToUInt16(raw, at + 4)))
                    : new Vector3(System.BitConverter.ToSingle(raw, at), System.BitConverter.ToSingle(raw, at + 4), System.BitConverter.ToSingle(raw, at + 8));
            }
            int[] idx;
            GraphicsBuffer ib = mesh.GetIndexBuffer();
            if (ib == null) { why = "it has no index buffer on the GPU"; return false; }
            try
            {
                if (mesh.indexFormat == IndexFormat.UInt16)
                {
                    var small = new ushort[ib.count];
                    ib.GetData(small);
                    idx = new int[small.Length];
                    for (int i = 0; i < small.Length; i++) idx[i] = small[i];
                }
                else
                {
                    idx = new int[ib.count];
                    ib.GetData(idx);
                }
            }
            finally { ib.Dispose(); }
            var list = new List<int>(idx.Length);
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                SubMeshDescriptor d = mesh.GetSubMesh(s);
                if (d.topology != MeshTopology.Triangles) continue;
                for (int k = 0; k + 2 < d.indexCount; k += 3)
                {
                    int at = d.indexStart + k;
                    if (at + 2 >= idx.Length) break;
                    int a = idx[at] + d.baseVertex, b = idx[at + 1] + d.baseVertex, c = idx[at + 2] + d.baseVertex;
                    if (a < 0 || b < 0 || c < 0 || a >= nv || b >= nv || c >= nv) { why = "its triangles point past its vertices"; return false; }
                    list.Add(a);
                    list.Add(b);
                    list.Add(c);
                }
            }
            tri = list.ToArray();
            return true;
        }

        // The box round a piece's corners in the frame turned deg about the world's up: its area, and where it lies.
        private static float Fit(Vector3[] w, List<int> piece, float deg, out float x0, out float x1, out float z0, out float z1)
        {
            float a = deg * Mathf.Deg2Rad, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
            x0 = z0 = float.MaxValue;
            x1 = z1 = float.MinValue;
            foreach (int i in piece)
            {
                float x = w[i].x * ca + w[i].z * sa, z = -w[i].x * sa + w[i].z * ca;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (z < z0) z0 = z;
                if (z > z1) z1 = z;
            }
            return (x1 - x0) * (z1 - z0);
        }

        private sealed class MeshPieces
        {
            public Vector3[] v;             // the mesh's vertices, in its own space
            public List<int>[] pieces;      // per piece the vertices it is made of
            public bool[] open;             // it has an edge with one triangle only: not a closed shell
            public List<int>[] tris;        // the open pieces' triangles (three vertices each)
        }

        // The mesh's pieces; null (and why) when it is one piece or cannot be read.
        // perMetre: the mesh's units in a metre of the world (corners within a millimetre are one).
        private static MeshPieces Pieces(Mesh mesh, float perMetre, out string why)
        {
            Vector3[] v;
            int[] tri;
            try
            {
                if (!ReadMesh(mesh, out v, out tri, out why)) return null;
            }
            catch (System.Exception e) { why = "reading it failed (" + e.GetType().Name + ": " + e.Message + ")"; return null; }
            if (v == null || tri == null || v.Length < 8 || tri.Length < 12) { why = "there was nothing to read"; return null; }
            // are these this mesh's positions? (a read the GPU does not allow comes back as zeros)
            Bounds bounds = mesh.bounds;
            Vector3 mn = v[0], mx = v[0];
            for (int i = 1; i < v.Length; i++) { mn = Vector3.Min(mn, v[i]); mx = Vector3.Max(mx, v[i]); }
            float tol = 0.02f * bounds.size.magnitude + 0.01f;
            if ((mn - bounds.min).magnitude > tol || (mx - bounds.max).magnitude > tol) { why = "the positions read back do not fit its bounds"; return null; }

            // vertices at one place are one corner; triangles join corners into pieces
            float q = 1000f * Mathf.Max(perMetre, 1e-6f);
            var place = new Dictionary<Vector3Int, int>(v.Length);
            var corner = new int[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                var key = new Vector3Int(Mathf.RoundToInt(v[i].x * q), Mathf.RoundToInt(v[i].y * q), Mathf.RoundToInt(v[i].z * q));
                if (!place.TryGetValue(key, out int id)) { id = place.Count; place[key] = id; }
                corner[i] = id;
            }
            var parent = new int[place.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            System.Func<int, int> find = a =>
            {
                while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
                return a;
            };
            var edges = new Dictionary<long, int>(tri.Length);
            void Edge(int a, int b)
            {
                long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                edges.TryGetValue(k, out int uses);
                edges[k] = uses + 1;
            }
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                int a = corner[tri[t]], b = corner[tri[t + 1]], c = corner[tri[t + 2]];
                int ra = find(a), rb = find(b), rc = find(c);
                parent[rb] = ra;
                parent[rc] = ra;
                if (a == b || b == c || a == c) continue;
                Edge(a, b);
                Edge(b, c);
                Edge(c, a);
            }
            var pieces = new Dictionary<int, List<int>>();
            for (int i = 0; i < v.Length; i++)
            {
                int root = find(corner[i]);
                if (!pieces.TryGetValue(root, out List<int> l)) pieces[root] = l = new List<int>(32);
                l.Add(i);
            }
            if (pieces.Count < 2) { why = OnePiece; return null; }
            var number = new Dictionary<int, int>(pieces.Count);
            var all = new List<int>[pieces.Count];
            foreach (KeyValuePair<int, List<int>> kv in pieces)
            {
                all[number.Count] = kv.Value;
                number[kv.Key] = number.Count;
            }
            // which pieces are not closed shells (an edge with one triangle only), and those pieces' triangles
            var open = new bool[all.Length];
            foreach (KeyValuePair<long, int> kv in edges)
                if (kv.Value == 1) open[number[find((int)(kv.Key >> 32))]] = true;
            var tris = new List<int>[all.Length];
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                int p = number[find(corner[tri[t]])];
                if (!open[p]) continue;
                if (tris[p] == null) tris[p] = new List<int>(64);
                tris[p].Add(tri[t]);
                tris[p].Add(tri[t + 1]);
                tris[p].Add(tri[t + 2]);
            }
            return new MeshPieces { v = v, pieces = all, open = open, tris = tris };
        }

        // The pieces' boxes where the object stands: each the smallest upright one, turned about the world's up
        // (these meshes come with y or with z as their up; the world's is the one rooms are turned about).
        // boxes: +-1 -> world. False when taking it apart gains nothing, or (why) must not be done.
        private static bool Rooms(MeshPieces m, Matrix4x4 l2w, Bounds bounds, List<Matrix4x4> boxes, out string why)
        {
            why = null;
            boxes.Clear();
            var w = new Vector3[m.v.Length];
            for (int i = 0; i < w.Length; i++) w[i] = l2w.MultiplyPoint3x4(m.v[i]);
            float held = 0f;
            for (int p = 0; p < m.pieces.Length; p++)
            {
                List<int> l = m.pieces[p];
                float best = float.MaxValue, deg = 0f;
                for (int d = 0; d < 90; d++)
                {
                    float area = Fit(w, l, d, out _, out _, out _, out _);
                    if (area < best) { best = area; deg = d; }
                }
                float whole = deg;
                for (int t = -9; t <= 9; t++)
                {
                    if (t == 0) continue;
                    float area = Fit(w, l, whole + t * 0.1f, out _, out _, out _, out _);
                    if (area < best) { best = area; deg = whole + t * 0.1f; }
                }
                Fit(w, l, deg, out float x0, out float x1, out float z0, out float z1);
                float y0 = float.MaxValue, y1 = float.MinValue;
                foreach (int i in l)
                {
                    if (w[i].y < y0) y0 = w[i].y;
                    if (w[i].y > y1) y1 = w[i].y;
                }
                var e = new Vector3((x1 - x0) * 0.5f, (y1 - y0) * 0.5f, (z1 - z0) * 0.5f);
                bool flat = e.x < 1e-3f || e.y < 1e-3f || e.z < 1e-3f;
                if (m.open[p])
                {
                    // Not a closed shell. One with a flaw (a missing triangle: Reserve's, Ground Zero's and
                    // Streets' merged meshes each have one or two) still encloses its room; a loose sheet - a plate
                    // that only with others walls a room in - encloses nothing seen from its own middle, and its
                    // box would leave that room outside: such a mesh stays whole.
                    Vector3 mid = Vector3.zero;
                    foreach (int i in l) mid += w[i];
                    mid /= l.Count;
                    double six = 0.0;
                    List<int> tr = m.tris[p];
                    for (int k = 0; tr != null && k + 2 < tr.Count; k += 3)
                        six += Vector3.Dot(w[tr[k]] - mid, Vector3.Cross(w[tr[k + 1]] - mid, w[tr[k + 2]] - mid));
                    if (flat || System.Math.Abs(six) / 6.0 < 0.02 * 8.0 * e.x * e.y * e.z)
                    {
                        why = "a loose sheet is among its " + m.pieces.Length + " pieces";
                        boxes.Clear();
                        return false;
                    }
                }
                if (flat) continue;                                            // a flat piece holds nothing
                float fa = deg * Mathf.Deg2Rad;
                var ux = new Vector3(Mathf.Cos(fa), 0f, Mathf.Sin(fa));
                var uz = new Vector3(-Mathf.Sin(fa), 0f, Mathf.Cos(fa));
                Vector3 c = ux * ((x0 + x1) * 0.5f) + new Vector3(0f, (y0 + y1) * 0.5f, 0f) + uz * ((z0 + z1) * 0.5f);
                Matrix4x4 box = Matrix4x4.identity;
                box.SetColumn(0, new Vector4(ux.x * e.x, 0f, ux.z * e.x, 0f));
                box.SetColumn(1, new Vector4(0f, e.y, 0f, 0f));
                box.SetColumn(2, new Vector4(uz.x * e.z, 0f, uz.z * e.z, 0f));
                box.SetColumn(3, new Vector4(c.x, c.y, c.z, 1f));
                boxes.Add(box);
                held += 8f * e.x * e.y * e.z;
            }
            if (boxes.Count < 2) return false;
            // Worth it only where the rooms leave most of the mesh's own box free. Interchange's hall meshes are
            // 3 - 12 pieces filling about all of theirs: nothing gained there, and room by room the map would
            // have more volumes than the 256 the shader holds.
            Vector3 bs = bounds.size;
            return held <= 0.5f * Mathf.Abs(l2w.determinant) * bs.x * bs.y * bs.z;
        }

        private static readonly List<Matrix4x4> _rooms = new List<Matrix4x4>();
        private const string OnePiece = "it is one piece";

        private static void AddIndoor(Matrix4x4 box)
        {
            Vector3 c = box.MultiplyPoint3x4(Vector3.zero);
            float r = Mathf.Sqrt(((Vector3)box.GetColumn(0)).sqrMagnitude + ((Vector3)box.GetColumn(1)).sqrMagnitude + ((Vector3)box.GetColumn(2)).sqrMagnitude);
            _indoorAll.Add(box.inverse);
            _indoorBall.Add(new Vector4(c.x, c.y, c.z, r));
            if (_indoorCount < MaxIndoor) _indoor[_indoorCount++] = box.inverse;
        }

        public static void CollectIndoor()
        {
            _indoorCount = 0;
            _indoorAll.Clear();
            _indoorBall.Clear();
            _indoorPickedAt = new Vector3(1e9f, 1e9f, 1e9f);
            _indoorPicks = 0;
            _indoorUsed = new bool[0];
            int found = 0, boxes = 0, split = 0, rooms = 0, whole = 0;
            string wholeWhy = null, largest = null;
            float largestSpan = 0f;
            var taken = new Dictionary<Mesh, MeshPieces>();                   // a mesh used by several objects is read once
            try
            {
                foreach (StencilShadow comp in Resources.FindObjectsOfTypeAll<StencilShadow>())
                {
                    if (comp == null || !comp.gameObject.scene.IsValid()) continue;
                    MeshFilter mf = comp.GetComponent<MeshFilter>();
                    Mesh mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    found++;
                    if (mesh.vertexCount <= 24) boxes++;
                    Bounds b = mesh.bounds;
                    if (b.extents.x < 1e-3f || b.extents.y < 1e-3f || b.extents.z < 1e-3f) continue;
                    Matrix4x4 l2w = comp.transform.localToWorldMatrix;
                    if (mesh.vertexCount > 24)
                    {
                        // more than a plain box: several rooms in one mesh? (Rooms)
                        if (!taken.TryGetValue(mesh, out MeshPieces parts))
                        {
                            Vector3 ls = comp.transform.lossyScale;
                            taken[mesh] = parts = Pieces(mesh, Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)), out string why);
                            if (parts == null && why != OnePiece && wholeWhy == null) wholeWhy = "'" + comp.name + "': " + why;
                        }
                        string sheet = null;
                        if (parts != null && Rooms(parts, l2w, b, _rooms, out sheet))
                        {
                            split++;
                            rooms += _rooms.Count;
                            Vector3 size = Vector3.Scale(b.size, comp.transform.lossyScale);
                            float span = Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.z));
                            if (span > largestSpan)
                            {
                                largestSpan = span;
                                largest = "'" + comp.name + "', " + _rooms.Count + " rooms in a box of " + Mathf.Abs(size.x).ToString("F0") + " x " + Mathf.Abs(size.y).ToString("F0") + " x " + Mathf.Abs(size.z).ToString("F0") + " m";
                            }
                            foreach (Matrix4x4 room in _rooms) AddIndoor(room);
                            continue;
                        }
                        if (sheet != null && wholeWhy == null) wholeWhy = "'" + comp.name + "': " + sheet;
                        whole++;
                    }
                    AddIndoor(l2w * Matrix4x4.TRS(b.center, Quaternion.identity, b.extents));
                }
            }
            catch (System.Exception e) { Plugin.MyLog.LogWarning("[TraceSix] Interior volumes failed: " + e.GetType().Name + ": " + e.Message); }
            Shader.SetGlobalMatrixArray("_GiIndoor", _indoor);
            Shader.SetGlobalFloat("_GiIndoorCount", _indoorCount);
            IndoorIndex(true);
            Plugin.MyLog.LogInfo("[TraceSix] Interior volumes: " + found + " objects, " + boxes + " of them plain boxes. " + split + " were several rooms in one mesh and are taken room by room ("
                + rooms + " rooms" + (largest != null ? "; the largest such mesh: " + largest : "") + "); " + whole + " other shapes count as the one box around them"
                + (wholeWhy != null ? " - one of them was not taken apart: " + wholeWhy : "") + ". " + _indoorAll.Count + " volumes in all, "
                + (_indoorAll.Count > MaxIndoor ? "the " + MaxIndoor + " nearest the camera" : "all") + " in use (room for " + MaxIndoor + ").");
        }

        //--- sun ---------------------------------------------------------------------------------------------

        // The game's one directional light (sun by day, moon by night), handed over by the sky object that
        // drives it (ITODSky.LightObject, which both of the game's skies have; TOD_Sky.Components.LightSource).
        // Called once per raid; a whole-scene search for lights is only the fallback, and never repeats.
        public static void FindSun(Object sky)
        {
            _sun = null;
            string how = "from the sky object";
            try
            {
                _sun = (sky as ITODSky)?.LightObject;
                if (_sun == null && sky is TOD_Sky tod && tod.Components != null) _sun = tod.Components.LightSource;
            }
            catch (System.Exception) { }
            if (_sun == null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (Light l in Object.FindObjectsOfType<Light>())
                    if (l.type == LightType.Directional && (_sun == null || l.intensity > _sun.intensity)) _sun = l;
                how = "by a scene search, " + sw.Elapsed.TotalMilliseconds.ToString("F1") + " ms";
            }
            Plugin.MyLog.LogInfo(_sun != null ? "[TraceSix] Sun light: '" + _sun.name + "' (" + how + ")." : "[TraceSix] No sun light found: the volumes get sky light only.");
        }

        private static void UpdateSun()
        {
            bool lit = _sun != null && _sun.isActiveAndEnabled && _sun.intensity > 0.001f;
            Vector3 d = lit ? -_sun.transform.forward : Vector3.up;
            Color col = lit ? _sun.color * _sun.intensity : Color.black;
            Shader.SetGlobalVector(IdSunDir, new Vector4(d.x, d.y, d.z, lit ? 1f : 0f));
            Shader.SetGlobalVector(IdSunColor, new Vector4(col.r, col.g, col.b, 0f));
        }

        //--- log ---------------------------------------------------------------------------------------------

        // What a finished volume holds, read back from the GPU: how many cells are open, and the average light
        // on upward-facing surfaces in the game's kind of number, next to the sky ambient and the sun.
        private static void LogContents(int c, RenderTexture volume)
        {
            Vector4 sunDir = Shader.GetGlobalVector(IdSunDir), sunCol = Shader.GetGlobalVector(IdSunColor);
            Vector3 skyUp = SkyAmbientUp();
            AsyncGPUReadback.Request(volume, 0, 0, NX, Base(c), NY, NZ * 2, NZ, TextureFormat.RGBAHalf, req =>   // the +y block
            {
                if (req.hasError) { Plugin.MyLog.LogWarning("[TraceSix] Light volume " + c + ": readback failed."); return; }
                long seen = 0, open = 0, dark = 0;
                double r = 0, g = 0, b = 0;
                for (int z = 0; z < req.layerCount; z++)
                {
                    var px = req.GetData<ushort>(z);
                    for (int i = 0; i + 3 < px.Length; i += 4 * 5)   // a sample
                    {
                        seen++;
                        if (Mathf.HalfToFloat(px[i + 3]) < 0.25f) continue;   // 0.6 indoors, 1 outdoors
                        open++;
                        float pr = Mathf.HalfToFloat(px[i]), pg = Mathf.HalfToFloat(px[i + 1]), pb = Mathf.HalfToFloat(px[i + 2]);
                        if (pr + pg + pb < 1e-6f) { dark++; continue; }
                        r += pr; g += pg; b += pb;
                    }
                }
                double lit = System.Math.Max(1, open - dark);
                Plugin.MyLog.LogInfo("[TraceSix] Light volume " + c + " (" + CellSize[c] + " m cells): " + (100.0 * open / System.Math.Max(1, seen)).ToString("F0")
                    + "% of cells open, " + (100.0 * dark / System.Math.Max(1, open)).ToString("F1") + "% of those get no light from above; the rest average ("
                    + Display(r / lit) + ", " + Display(g / lit) + ", " + Display(b / lit) + ") on upward surfaces. Sky ambient straight up ("
                    + skyUp.x.ToString("F3") + ", " + skyUp.y.ToString("F3") + ", " + skyUp.z.ToString("F3") + "); sun colour ("
                    + sunCol.x.ToString("F2") + ", " + sunCol.y.ToString("F2") + ", " + sunCol.z.ToString("F2") + "), "
                    + (sunDir.w > 0.5f ? (Mathf.Asin(Mathf.Clamp(sunDir.y, -1f, 1f)) * Mathf.Rad2Deg).ToString("F0") + " deg above the horizon." : "off."));
            });
        }

        // Stored linear light -> the game's kind of number.
        private static string Display(double stored) => System.Math.Pow(System.Math.Max(stored, 0.0) / Store, 1.0 / 2.2).ToString("F3");

        // The game's ambient for a surface facing straight up, as its ambient pass computes it.
        internal static Vector3 SkyUp() => SkyAmbientUp();
        private static Vector3 SkyAmbientUp()
        {
            Vector4 ar = Shader.GetGlobalVector("_SHAr"), ag = Shader.GetGlobalVector("_SHAg"), ab = Shader.GetGlobalVector("_SHAb"), sc = Shader.GetGlobalVector("_SHC");
            return new Vector3(Gamma(ar.y + ar.w - sc.x), Gamma(ag.y + ag.w - sc.y), Gamma(ab.y + ab.w - sc.z));
        }

        private static float Gamma(float v) => Mathf.Max(1.055f * Mathf.Pow(Mathf.Max(v, 0f), 0.416667f) - 0.055f, 0f);
    }
}
