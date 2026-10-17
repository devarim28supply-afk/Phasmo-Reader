using System.Text;
using PhasmoReader;

// Outside reader for Phasmophobia (Unity IL2CPP): looks at the running game's memory, read-only.
//   PhasmoReader probe [n]   find AudioCullingManager and print its audio list (for working out the layout)
//   PhasmoReader names       try Unity native layouts for each source's GameObject name

const string Game = "Phasmophobia";
string mode = args.Length > 0 ? args[0] : "feed";

//   PhasmoReader [feed] [file]   (default) write the live sound feed for the overlay, waits for the game
if (mode == "feed")
{
    string path = args.Length > 1 ? args[1] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "soundfeed.txt"));
    using var one = new Mutex(true, "PhasmoReader-" + path.ToLowerInvariant().Replace('\\', '/'), out bool firstRun);
    if (!firstRun) { Console.WriteLine("The reader is already running."); return 0; }
    var logStream = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, "reader.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    Console.SetOut(new Tee(Console.Out, new StreamWriter(logStream) { AutoFlush = true }));
    while (true)
    {
        Mem? g;
        while ((g = Mem.Open(Game)) == null) Thread.Sleep(2000);
        using (g)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} game process {g.Pid} opened READ-ONLY");
            Thread.Sleep(5000);
            var feed = new PhasmoFeed(g, path, Console.Out);
            try
            {
                Console.WriteLine("writing " + path);
                while (g.Alive && !feed.FindClasses()) { Console.WriteLine("  game still loading, trying again"); Thread.Sleep(5000); }
                var rescan = System.Diagnostics.Stopwatch.StartNew();
                bool first = true;
                while (g.Alive)
                {
                    if (first || rescan.ElapsedMilliseconds > 20000)
                    {
                        feed.Scan();
                        if (first) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {feed.Count} sound sources, reading");
                        rescan.Restart(); first = false;
                    }
                    feed.Tick();
                    Thread.Sleep(50);
                }
            }
            catch (Exception e) { Console.WriteLine("reader stopped: " + e.Message); }
            finally { feed.Close(); }
        }
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} game closed, waiting for it to start again");
        while (System.Diagnostics.Process.GetProcessesByName(Game).Length > 0) Thread.Sleep(2000);
    }
}

using var m = Mem.Open(Game);
if (m == null) { Console.WriteLine("Phasmophobia is not running."); return 1; }
Console.WriteLine($"game process {m.Pid} opened READ-ONLY");
var (ga, gaSize) = m.Module("GameAssembly.dll");
Console.WriteLine($"GameAssembly.dll at 0x{ga:X}, {gaSize / 1048576} MB");

// Il2CppClass (metadata v31): name 0x10, namespaze 0x18, klass (= itself) 0x78, static_fields 0xB8
const int KName = 0x10, KNs = 0x18, KSelf = 0x78, KStatics = 0xB8;

ulong FindClass(string name)
{
    var strs = m.FindBytes(Encoding.ASCII.GetBytes(name + "\0"), 50);
    var set = new HashSet<ulong>(strs);
    foreach (var (at, _) in m.FindAny(set, 2000))
    {
        ulong k = at - KName;
        if (m.Ptr(k + KSelf) == k) return k;
    }
    return 0;
}

var sw = System.Diagnostics.Stopwatch.StartNew();
ulong acm = FindClass("AudioCullingManager");
Console.WriteLine($"AudioCullingManager class 0x{acm:X} ({sw.ElapsedMilliseconds} ms), namespace \"{m.CStr(m.Ptr(acm + KNs))}\"");
if (acm == 0) return 2;
ulong st = m.Ptr(acm + KStatics);
ulong list = m.Ptr(st), nlist = m.Ptr(st + 8), inst = m.Ptr(st + 0x10);
Console.WriteLine($"statics 0x{st:X}: List<AudioSource> 0x{list:X}, NativeList data 0x{nlist:X}, instance 0x{inst:X}");

// List<T>: _items 0x10, _size 0x18; T[]: length 0x18, elements from 0x20
ulong items = m.Ptr(list + 0x10);
int size = m.I32(list + 0x18), cap = m.I32(items + 0x18);
// UnsafeList<T>: Ptr 0x0, length 0x8, capacity 0xC
ulong dptr = m.Ptr(nlist);
int dlen = m.I32(nlist + 8), dcap = m.I32(nlist + 12);
Console.WriteLine($"sources: {size} (array {cap}); data entries: {dlen} (cap {dcap}) at 0x{dptr:X}");

// world position from a TransformHierarchy: local TRS = 3 x float4 (t, q, s) per node, parent indices = int per node
(float x, float y, float z)? WorldPos(ulong loc, ulong par, int idx)
{
    var t0 = m.Bytes(loc + (ulong)idx * 0x30, 0x30); if (t0.Length == 0) return null;
    double x = BitConverter.ToSingle(t0, 0), y = BitConverter.ToSingle(t0, 4), z = BitConverter.ToSingle(t0, 8);
    int p = m.I32(par + (ulong)idx * 4);
    for (int d = 0; p >= 0 && d < 64; d++)
    {
        var n = m.Bytes(loc + (ulong)p * 0x30, 0x30); if (n.Length == 0) return null;
        double qx = BitConverter.ToSingle(n, 16), qy = BitConverter.ToSingle(n, 20), qz = BitConverter.ToSingle(n, 24), qw = BitConverter.ToSingle(n, 28);
        x *= BitConverter.ToSingle(n, 32); y *= BitConverter.ToSingle(n, 36); z *= BitConverter.ToSingle(n, 40);
        // rotate (x,y,z) by quaternion q: v + 2w(q x v) + 2 q x (q x v)
        double cx = qy * z - qz * y, cy = qz * x - qx * z, cz = qx * y - qy * x;
        double ccx = qy * cz - qz * cy, ccy = qz * cx - qx * cz, ccz = qx * cy - qy * cx;
        x += 2 * (qw * cx + ccx) + BitConverter.ToSingle(n, 0);
        y += 2 * (qw * cy + ccy) + BitConverter.ToSingle(n, 4);
        z += 2 * (qw * cz + ccz) + BitConverter.ToSingle(n, 8);
        p = m.I32(par + (ulong)p * 4);
    }
    return ((float)x, (float)y, (float)z);
}

// listeners: every UnityEngine.AudioListener object with its parents + world position
if (mode == "listener")
{
    var strs = m.FindBytes(Encoding.ASCII.GetBytes("AudioListener\0"), 200);
    var cls = new HashSet<ulong>();
    foreach (var (at, _) in m.FindAny(new HashSet<ulong>(strs), 5000))
    {
        ulong k = at - KName;
        if (m.Ptr(k + KSelf) == k && m.CStr(m.Ptr(k + KNs)) == "UnityEngine") cls.Add(k);
    }
    foreach (var (o, _) in m.FindAny(cls, 1000))
    {
        ulong n = m.Ptr(o + 0x10); if (n == 0) continue;
        ulong g = m.Ptr(n + 0x30);
        string name = m.CStr(m.Ptr(g + 0x60), 64);
        if (name.Length == 0 || !name.All(c => c >= 32 && c < 127)) continue;
        ulong tr = m.Ptr(m.Ptr(g + 0x30) + 8), hier = m.Ptr(tr + 0x38); int idx = m.I32(tr + 0x40);
        ulong par = m.Ptr(hier + 0x20), arr = m.Ptr(hier + 0x30);
        var names = new List<string>(); int pi = m.I32(par + (ulong)idx * 4);
        for (int d = 0; pi >= 0 && d < 5; d++) { names.Add(m.CStr(m.Ptr(m.Ptr(m.Ptr(arr + (ulong)pi * 8) + 0x30) + 0x60), 48)); pi = m.I32(par + (ulong)pi * 4); }
        var w = WorldPos(m.Ptr(hier + 0x18), par, idx);
        var nb = m.Bytes(n, 0x60);
        Console.WriteLine($"listener {name} < {string.Join(" < ", names)} @ {w?.x:0.0} {w?.y:0.0} {w?.z:0.0}  native bytes 0x38..0x40: {BitConverter.ToString(nb, 0x38, 8)}");
    }
    return 0;
}

// record: every sound that starts playing -> time, name, parents, position (finds sources again every 20 s)
if (mode == "record")
{
    string outPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "phasmo-record.txt");
    using var log = new StreamWriter(outPath, true) { AutoFlush = true };
    var strs = m.FindBytes(Encoding.ASCII.GetBytes("AudioSource\0"), 200);
    var cls = new HashSet<ulong>();
    foreach (var (at, _) in m.FindAny(new HashSet<ulong>(strs), 5000))
    {
        ulong k = at - KName;
        if (m.Ptr(k + KSelf) == k && m.CStr(m.Ptr(k + KNs)) == "UnityEngine") cls.Add(k);
    }
    Console.WriteLine($"recording to {outPath} (Ctrl+C to stop)");
    log.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} recording started");
    var natives = new List<ulong>();
    var playing = new HashSet<ulong>();
    var info = new Dictionary<ulong, string>();
    var rescan = System.Diagnostics.Stopwatch.StartNew();
    bool first = true;
    while (m.Alive)
    {
        if (first || rescan.ElapsedMilliseconds > 20000)
        {
            natives = m.FindAny(cls, 20000).Select(x => m.Ptr(x.at + 0x10)).Where(n => n != 0).Distinct().ToList();
            rescan.Restart(); first = false;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {natives.Count} sources");
        }
        foreach (var n in natives)
        {
            bool on = m.Ptr(n + 0x380) != 0;
            if (!on) { playing.Remove(n); continue; }
            if (!playing.Add(n)) continue;
            ulong g = m.Ptr(n + 0x30);
            string name = m.CStr(m.Ptr(g + 0x60), 64);
            if (name.Length == 0 || !name.All(c => c >= 32 && c < 127)) continue;
            ulong tr = m.Ptr(m.Ptr(g + 0x30) + 8), hier = m.Ptr(tr + 0x38); int idx = m.I32(tr + 0x40);
            ulong par = m.Ptr(hier + 0x20), arr = m.Ptr(hier + 0x30);
            var names = new List<string>();
            int pi = m.I32(par + (ulong)idx * 4);
            for (int d = 0; pi >= 0 && d < 4; d++) { names.Add(m.CStr(m.Ptr(m.Ptr(m.Ptr(arr + (ulong)pi * 8) + 0x30) + 0x60), 48)); pi = m.I32(par + (ulong)pi * 4); }
            var w = WorldPos(m.Ptr(hier + 0x18), par, idx);
            string line = $"{DateTime.Now:HH:mm:ss.f}  {name}  <  {string.Join(" < ", names)}  @ {w?.x:0.0} {w?.y:0.0} {w?.z:0.0}";
            log.WriteLine(line);
            Console.WriteLine(line);
        }
        Thread.Sleep(100);
    }
    return 0;
}

// every managed AudioSource object (object header = its Il2CppClass*) -> native -> GameObject name
if (mode == "all")
{
    var strs = m.FindBytes(Encoding.ASCII.GetBytes("AudioSource\0"), 200);
    var cands = new HashSet<ulong>();
    foreach (var (at, _) in m.FindAny(new HashSet<ulong>(strs), 5000))
    {
        ulong k = at - KName;
        if (m.Ptr(k + KSelf) == k && m.CStr(m.Ptr(k + KNs)) == "UnityEngine") cands.Add(k);
    }
    Console.WriteLine($"AudioSource class: {string.Join(", ", cands.Select(c => $"0x{c:X}"))}");
    var objs = m.FindAny(cands, 20000).Select(x => x.at).Where(o => m.Ptr(o + 0x10) != 0).ToList();
    Console.WriteLine($"{objs.Count} AudioSource objects");
    var byName = new Dictionary<string, int>();
    foreach (var o in objs)
    {
        ulong native = m.Ptr(o + 0x10), g = m.Ptr(native + 0x30);
        string n = m.CStr(m.Ptr(g + 0x60), 64);
        byName[n] = byName.GetValueOrDefault(n) + 1;
    }
    foreach (var (n, c) in byName.OrderByDescending(x => x.Value).Take(args.Length > 1 ? 0 : 400)) Console.WriteLine($"{c,4}  {n}");
    if (args.Length > 1 && args[1] == "playing")
    {
        // +0x380 (channel?) set = playing; look for the rain sources' culling-list position inside native / GameObject / Transform
        foreach (var o in objs)
        {
            ulong native = m.Ptr(o + 0x10), g = m.Ptr(native + 0x30);
            string n = m.CStr(m.Ptr(g + 0x60), 64);
            if (n.Length < 3 || !n.All(c => c >= 32 && c < 127) || m.Ptr(native + 0x380) == 0) continue;
            Console.WriteLine($"PLAYING {n}  native 0x{native:X} go 0x{g:X}");
            // clip name: some pointer field -> object with a name char* (scan offsets)
            var nb = m.Bytes(native, 0x600);
            for (int q = 0x18; q + 8 <= nb.Length; q += 8)
            {
                ulong p = BitConverter.ToUInt64(nb, q);
                if (p < 0x10000 || p > 0x7FF000000000 || p == g) continue;
                foreach (int k in new[] { 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60, 0x68 })
                {
                    string s = m.CStr(m.Ptr(p + (ulong)k), 48);
                    if (s.Length > 3 && s.All(c => c >= 32 && c < 127) && s != n) Console.WriteLine($"     native+0x{q:X} -> +0x{k:X}: {s}");
                }
            }
            // parent GameObject name via the hierarchy's transform array
            ulong tr = m.Ptr(m.Ptr(g + 0x30) + 8), hier = m.Ptr(tr + 0x38); int idx = m.I32(tr + 0x40);
            foreach (int ta in new[] { 0x28, 0x30, 0x38, 0x40 })
            {
                ulong arr = m.Ptr(hier + (ulong)ta);
                if (m.Ptr(arr + (ulong)idx * 8) != tr) continue;
                int pi = m.I32(m.Ptr(hier + 0x20) + (ulong)idx * 4);
                var names = new List<string>();
                for (int d = 0; pi >= 0 && d < 4; d++)
                {
                    ulong pt = m.Ptr(arr + (ulong)pi * 8);
                    names.Add(m.CStr(m.Ptr(m.Ptr(pt + 0x30) + 0x60), 48));
                    pi = m.I32(m.Ptr(hier + 0x20) + (ulong)pi * 4);
                }
                Console.WriteLine($"     parents (hierarchy+0x{ta:X}): {string.Join(" < ", names)}");
            }
        }
        for (int i = 0; i < Math.Min(3, size); i++)
        {
            ulong native = m.Ptr(m.Ptr(items + 0x20 + (ulong)i * 8) + 0x10), g = m.Ptr(native + 0x30);
            var e = m.Bytes(dptr + (ulong)i * 0x20, 12);
            float px = BitConverter.ToSingle(e, 0), pz = BitConverter.ToSingle(e, 8);
            Console.WriteLine($"rain {i} pos {px:0.00} {BitConverter.ToSingle(e, 4):0.00} {pz:0.00}");
            // GameObject component list: try ptr at g+0x30 -> array of {typeinfo, Component*} pairs
            foreach (var (label, baseAddr, span) in new[] { ("native", native, 0x800), ("go", g, 0x200) })
            {
                var b = m.Bytes(baseAddr, span);
                for (int q = 0; q + 12 <= b.Length; q += 4)
                    if (Math.Abs(BitConverter.ToSingle(b, q) - px) < 0.05 && Math.Abs(BitConverter.ToSingle(b, q + 8) - pz) < 0.05)
                        Console.WriteLine($"   x/z match in {label}+0x{q:X}");
            }
            // Transform = first component; world position from its TransformHierarchy (try a few layouts)
            ulong tr = m.Ptr(m.Ptr(g + 0x30) + 8);
            foreach (int ho in new[] { 0x38, 0x40, 0x30 })
                foreach (int lo in new[] { 0x18, 0x20, 0x28 })
                {
                    ulong hier = m.Ptr(tr + (ulong)ho); int idx = m.I32(tr + (ulong)ho + 8);
                    ulong loc = m.Ptr(hier + (ulong)lo), par = m.Ptr(hier + (ulong)lo + 8);
                    if (hier == 0 || loc == 0 || idx < 0 || idx > 100000) continue;
                    var w = WorldPos(loc, par, idx);
                    if (w != null && Math.Abs(w.Value.x - px) < 0.1 && Math.Abs(w.Value.z - pz) < 0.1)
                        Console.WriteLine($"   WORLD POS MATCH: transform+0x{ho:X} hierarchy, +0x{lo:X} local TRS / +0x{lo + 8:X} parents -> {w.Value.x:0.00} {w.Value.y:0.00} {w.Value.z:0.00}");
                }
            ulong comps = m.Ptr(g + 0x30);
            for (int c = 0; c < 6; c++)
            {
                ulong comp = m.Ptr(comps + (ulong)c * 16 + 8);
                var b = m.Bytes(comp, 0x200);
                for (int q = 0; q + 12 <= b.Length; q += 4)
                    if (Math.Abs(BitConverter.ToSingle(b, q) - px) < 0.05 && Math.Abs(BitConverter.ToSingle(b, q + 8) - pz) < 0.05)
                        Console.WriteLine($"   x/z match in component {c} (0x{comp:X}) +0x{q:X}");
            }
        }
    }
    if (args.Length > 1 && args[1] == "diff")
    {
        // offsets in the native AudioSource that are set on every (always playing) rain source but rarely on others
        const int N = 0x600;
        var rain = new List<byte[]>(); var rest = new List<byte[]>();
        var self = new Dictionary<int, int>();
        foreach (var o in objs)
        {
            ulong native = m.Ptr(o + 0x10), g = m.Ptr(native + 0x30);
            string n = m.CStr(m.Ptr(g + 0x60), 64);
            if (n.Length < 3 || !n.All(c => c >= 32 && c < 127)) continue;
            var b = m.Bytes(native, N); if (b.Length == 0) continue;
            (n == "Window_Rain" ? rain : rest).Add(b);
            for (int q = 0; q < N; q += 8) if (BitConverter.ToUInt64(b, q) == o) self[q] = self.GetValueOrDefault(q) + 1;
        }
        Console.WriteLine($"rain {rain.Count}, others {rest.Count}; native offsets holding the managed object: {string.Join(" ", self.Select(x => $"0x{x.Key:X}={x.Value}"))}");
        for (int q = 0; q < N; q += 4)
        {
            int rz = rain.Count(b => BitConverter.ToUInt32(b, q) != 0), oz = rest.Count(b => BitConverter.ToUInt32(b, q) != 0);
            bool rainSame = rain.Select(b => BitConverter.ToUInt32(b, q)).Distinct().Count() == 1;
            if (rz == rain.Count && oz < rest.Count * 0.3 || rz == 0 && oz > rest.Count * 0.7)
                Console.WriteLine($"  +0x{q:X3}: rain set {rz}/{rain.Count} ({(rainSame ? "same " + BitConverter.ToUInt32(rain[0], q).ToString("X") : "varies")}), others set {oz}/{rest.Count}");
        }
    }
    return 0;
}

// steps [seconds]: player footstep sources (FootstepAudio): which native AudioSource bytes change while walking
// (the playing flag +0x380 stays 0 for them, so they are probably PlayOneShot)
if (mode == "steps")
{
    var cls = new HashSet<ulong>();
    foreach (var (at, _) in m.FindAny(new HashSet<ulong>(m.FindBytes(Encoding.ASCII.GetBytes("AudioSource\0"), 200)), 5000))
    {
        ulong k = at - KName;
        if (m.Ptr(k + KSelf) == k && m.CStr(m.Ptr(k + KNs)) == "UnityEngine") cls.Add(k);
    }
    var srcs = new List<ulong>();
    foreach (var n in m.FindAny(cls, 20000).Select(x => m.Ptr(x.at + 0x10)).Where(n => n != 0).Distinct())
    {
        ulong g = m.Ptr(n + 0x30), tr = m.Ptr(m.Ptr(g + 0x30) + 8), hier = m.Ptr(tr + 0x38);
        int idx = m.I32(tr + 0x40);
        if (hier == 0 || idx < 0) continue;
        int pi = m.I32(m.Ptr(hier + 0x20) + (ulong)idx * 4);
        if (pi < 0) continue;
        string p0 = m.CStr(m.Ptr(m.Ptr(m.Ptr(m.Ptr(hier + 0x30) + (ulong)pi * 8) + 0x30) + 0x60), 48);
        if (p0 == "FootstepAudio") srcs.Add(n);
    }
    Console.WriteLine($"{srcs.Count} FootstepAudio sources: walk now (seconds: {(args.Length > 1 ? args[1] : "30")})");
    const int N = 0x800;
    var prev = srcs.ToDictionary(n => n, n => m.Bytes(n, N));
    var changes = srcs.ToDictionary(n => n, n => new int[N / 4]);
    var vals = srcs.ToDictionary(n => n, n => new Dictionary<int, HashSet<uint>>());
    var end = DateTime.Now.AddSeconds(args.Length > 1 ? int.Parse(args[1]) : 30);
    while (DateTime.Now < end)
    {
        foreach (var n in srcs)
        {
            var b = m.Bytes(n, N); if (b.Length < N) continue;
            for (int q = 0; q < N; q += 4)
            {
                uint a = BitConverter.ToUInt32(prev[n], q), c = BitConverter.ToUInt32(b, q);
                if (a == c) continue;
                changes[n][q / 4]++;
                var set = vals[n].TryGetValue(q, out var s) ? s : vals[n][q] = new();
                if (set.Count < 6) set.Add(c);
            }
            prev[n] = b;
        }
        Thread.Sleep(20);
    }
    foreach (var n in srcs)
    {
        Console.WriteLine($"source 0x{n:X}:");
        for (int q = 0; q < N; q += 4)
            if (changes[n][q / 4] > 0)
                Console.WriteLine($"  +0x{q:X3} changed {changes[n][q / 4],4}x  e.g. {string.Join(" ", vals[n][q].Select(v => v.ToString("X")))}");
    }
    return 0;
}

// clips [seconds]: every sound that starts -> GameObject < parent : clip name (native +0x338 = the clip's instance id,
// native AudioClip +8 = instance id, +0x30 = name). Sources and clips found again every 20 s.
if (mode == "clips")
{
    HashSet<ulong> Cls(string name)
    {
        var set = new HashSet<ulong>();
        foreach (var (at, _) in m.FindAny(new HashSet<ulong>(m.FindBytes(Encoding.ASCII.GetBytes(name + "\0"), 200)), 5000))
        {
            ulong k = at - KName;
            if (m.Ptr(k + KSelf) == k && m.CStr(m.Ptr(k + KNs)) == "UnityEngine") set.Add(k);
        }
        return set;
    }
    var srcCls = Cls("AudioSource"); var clipCls = Cls("AudioClip");
    var names = new Dictionary<int, string>();
    var natives = new List<ulong>();
    var chan = new Dictionary<ulong, ulong>();
    var end = DateTime.Now.AddSeconds(args.Length > 1 ? int.Parse(args[1]) : 1800);
    var rescan = System.Diagnostics.Stopwatch.StartNew(); bool first = true;
    while (m.Alive && DateTime.Now < end)
    {
        if (first || rescan.ElapsedMilliseconds > 20000)
        {
            natives = m.FindAny(srcCls, 20000).Select(x => m.Ptr(x.at + 0x10)).Where(n => n != 0).Distinct().ToList();
            foreach (var c in m.FindAny(clipCls, 50000).Select(x => m.Ptr(x.at + 0x10)).Where(n => n != 0).Distinct())
                names.TryAdd(m.I32(c + 8), m.CStr(m.Ptr(c + 0x30), 96));
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {natives.Count} sources, {names.Count} clips");
            rescan.Restart(); first = false;
        }
        foreach (var n in natives)
        {
            ulong ch = m.Ptr(n + 0x380);
            if (ch == 0) { chan.Remove(n); continue; }
            if (chan.TryGetValue(n, out var was) && was == ch) continue;
            chan[n] = ch;
            ulong g = m.Ptr(n + 0x30);
            string go = m.CStr(m.Ptr(g + 0x60), 64);
            ulong tr = m.Ptr(m.Ptr(g + 0x30) + 8), hier = m.Ptr(tr + 0x38); int idx = m.I32(tr + 0x40);
            int pi = hier == 0 || idx < 0 ? -1 : m.I32(m.Ptr(hier + 0x20) + (ulong)idx * 4);
            string p0 = pi < 0 ? "" : m.CStr(m.Ptr(m.Ptr(m.Ptr(m.Ptr(hier + 0x30) + (ulong)pi * 8) + 0x30) + 0x60), 48);
            int id = m.I32(n + 0x338);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.f}  {go} < {p0} : {names.GetValueOrDefault(id, "?" + id)}");
        }
        Thread.Sleep(50);
    }
    return 0;
}

// voice [seconds]: find where a playing AudioSource keeps its AudioClip, then log the clip name of every
// ghost voice that plays (spirit box answers, whispers), for the subtitle table
if (mode == "voice")
{
    HashSet<ulong> Cls(string name)
    {
        var set = new HashSet<ulong>();
        foreach (var (at, _) in m.FindAny(new HashSet<ulong>(m.FindBytes(Encoding.ASCII.GetBytes(name + "\0"), 200)), 5000))
        {
            ulong k = at - KName;
            if (m.Ptr(k + KSelf) == k && m.CStr(m.Ptr(k + KNs)) == "UnityEngine") set.Add(k);
        }
        return set;
    }
    bool Ok(string s) => s.Length > 2 && s.All(c => c >= 32 && c < 127);

    // native AudioClips: managed AudioClip +0x10; name = a char* somewhere near the start (count which offset works)
    var clips = m.FindAny(Cls("AudioClip"), 50000).Select(x => m.Ptr(x.at + 0x10)).Where(n => n != 0).Distinct().ToList();
    var nameAt = new Dictionary<int, int>();
    foreach (var c in clips.Take(400))
        for (int q = 0x8; q < 0x100; q += 8) if (Ok(m.CStr(m.Ptr(c + (ulong)q), 96))) nameAt[q] = nameAt.GetValueOrDefault(q) + 1;
    int no = nameAt.Count == 0 ? 0 : nameAt.MaxBy(x => x.Value).Key;
    Console.WriteLine($"{clips.Count} AudioClips; name char* offsets: {string.Join(" ", nameAt.OrderByDescending(x => x.Value).Take(5).Select(x => $"+0x{x.Key:X}={x.Value}"))} -> using +0x{no:X}");
    var clipSet = new HashSet<ulong>(clips);
    var byId = new Dictionary<int, ulong>();
    foreach (var c in clips) byId.TryAdd(m.I32(c + 8), c);
    string ClipName(ulong c) => m.CStr(m.Ptr(c + (ulong)no), 96);
    foreach (var c in clips.Where(c => Regex(ClipName(c))).Take(60)) Console.WriteLine($"  clip {ClipName(c)}");
    static bool Regex(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "spirit|evp|whisper|ghost|ouija|voice|answer", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    var natives = m.FindAny(Cls("AudioSource"), 20000).Select(x => m.Ptr(x.at + 0x10)).Where(n => n != 0).Distinct().ToList();
    Console.WriteLine($"{natives.Count} AudioSources; watching (Ctrl+C to stop)");
    var seen = new HashSet<ulong>();
    var until = DateTime.Now.AddSeconds(args.Length > 1 ? int.Parse(args[1]) : 900);
    while (m.Alive && DateTime.Now < until)
    {
        foreach (var n in natives)
        {
            if (m.Ptr(n + 0x380) == 0) { seen.Remove(n); continue; }
            if (!seen.Add(n)) continue;
            ulong g = m.Ptr(n + 0x30);
            string go = m.CStr(m.Ptr(g + 0x60), 64);
            if (!Ok(go)) continue;
            // the clip: a direct pointer to a native AudioClip, a pointer one step away, or its instance id
            var b = m.Bytes(n, 0x800);
            var hits = new List<string>();
            for (int q = 0; q + 8 <= b.Length; q += 4)
            {
                ulong v = BitConverter.ToUInt64(b, q);
                int id = BitConverter.ToInt32(b, q);
                if (q % 8 == 0 && clipSet.Contains(v)) hits.Add($"ptr+0x{q:X}={ClipName(v)}");
                else if (q % 8 == 0 && v > 0x10000 && v < 0x7FF000000000)
                    for (int k = 0; k < 0x80; k += 8) { ulong v2 = m.Ptr(v + (ulong)k); if (clipSet.Contains(v2)) hits.Add($"ptr+0x{q:X}->+0x{k:X}={ClipName(v2)}"); }
                if (id != 0 && byId.TryGetValue(id, out var ci)) hits.Add($"id+0x{q:X}={ClipName(ci)}");
            }
            if (args.Length > 2 && args[2] == "at")
            {
                // only hits that name a real clip (PPtr = the clip's instance id, native AudioClip +8)
                var good = new List<string>();
                for (int q = 0; q + 4 <= b.Length; q += 4)
                {
                    int id = BitConverter.ToInt32(b, q);
                    if (id != 0 && byId.TryGetValue(id, out var ci) && Ok(ClipName(ci))) good.Add($"id+0x{q:X}={ClipName(ci)}");
                }
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} {go}: {string.Join("  ", good)}");
                continue;
            }
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} {go}: {string.Join("  ", hits.Distinct().Take(8))}");
        }
        Thread.Sleep(100);
    }
    return 0;
}

if (mode == "names")
{
    for (int i = 0; i < size; i++)
    {
        ulong src = m.Ptr(items + 0x20 + (ulong)i * 8), native = m.Ptr(src + 0x10);
        var sb = new StringBuilder();
        foreach (int go in new[] { 0x30, 0x28, 0x38 })
            foreach (int nm in new[] { 0x60, 0x68, 0x58, 0x50 })
            {
                ulong g = m.Ptr(native + (ulong)go);
                string n = m.CStr(m.Ptr(g + (ulong)nm), 64);
                if (n.Length > 2 && n.All(c => c >= 32 && c < 127)) sb.Append($" [go+0x{go:X} name+0x{nm:X}] {n}");
            }
        Console.WriteLine($"{i,3}{sb}");
    }
    return 0;
}

for (int i = 0; i < Math.Min(Math.Max(size, dlen), args.Length > 1 ? int.Parse(args[1]) : 40); i++)
{
    ulong src = m.Ptr(items + 0x20 + (ulong)i * 8);
    ulong native = src == 0 ? 0 : m.Ptr(src + 0x10);
    var e = m.Bytes(dptr + (ulong)i * 0x20, 0x20);
    string data = e.Length == 0 ? "-" :
        $"pos {BitConverter.ToSingle(e, 0),8:0.0} {BitConverter.ToSingle(e, 4),7:0.0} {BitConverter.ToSingle(e, 8),8:0.0}  " +
        $"f {BitConverter.ToSingle(e, 12),7:0.00} {BitConverter.ToSingle(e, 16),7:0.00} {BitConverter.ToSingle(e, 20),7:0.00} {BitConverter.ToSingle(e, 24),7:0.00}  " +
        $"b {e[28]} {e[29]} {e[30]}";
    Console.WriteLine($"{i,3} src 0x{src:X} native 0x{native:X}  {data}");
}
return 0;

sealed class Tee(TextWriter a, TextWriter b) : TextWriter
{
    public override System.Text.Encoding Encoding => a.Encoding;
    public override void Write(char c) { a.Write(c); b.Write(c); }
    public override void Write(string? s) { a.Write(s); b.Write(s); }
    public override void WriteLine(string? s) { a.WriteLine(s); b.WriteLine(s); }
    public override void Flush() { a.Flush(); b.Flush(); }
}
