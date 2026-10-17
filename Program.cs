using System.Text;
using PhasmoReader;

// Outside reader for Phasmophobia (Unity IL2CPP): looks at the running game's memory, read-only.
//   PhasmoReader probe [n]   find AudioCullingManager and print its audio list (for working out the layout)
//   PhasmoReader names       try Unity native layouts for each source's GameObject name

const string Game = "Phasmophobia";
string mode = args.Length > 0 ? args[0] : "probe";

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
