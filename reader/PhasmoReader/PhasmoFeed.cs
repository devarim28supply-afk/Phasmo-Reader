using System.Globalization;
using System.Text;

namespace PhasmoReader;

// Writes the same soundfeed.txt lines as the Other Side reader, so the same overlay shows Phasmophobia:
//   L  x y z yaw                  listener = your camera (Unreal-style: cm, X forward, Y right, Z up)
//   S  id name x y z range src    a sound started (id = native AudioSource)
//   E  id                         it stopped
//   2  name src                   a sound with no position (in your head: heartbeat)
// src = ghost | player | own | other. Names are chosen so the overlay's built-in rules label + color them
// (Footstep_Ghost = red GHOST STEPS, Footsteps_Player = blue Player steps, Ambient_* = hidden by config).
//
// Read-only, from outside. Layout worked out live on the 2026-10 build (PhasmoReader probe/record):
//   managed AudioSource +0x10 = native; native +0x380 != 0 while playing; native +0x30 = GameObject,
//   GameObject +0x60 = name, +0x30 = components (first = Transform); Transform +0x38 = TransformHierarchy,
//   +0x40 = index; hierarchy +0x18 = local TRS (t, q, s float4 each), +0x20 = parent indices, +0x30 = transforms.
sealed class PhasmoFeed
{
    const int KName = 0x10, KNs = 0x18, KSelf = 0x78;
    readonly Mem m;
    readonly StreamWriter w;
    readonly TextWriter log;
    readonly HashSet<ulong> sourceCls = new(), listenerCls = new();
    List<ulong> natives = new();
    ulong listener;                                           // native AudioListener on your camera
    readonly Dictionary<ulong, (string name, string src)> playing = new();
    readonly Dictionary<ulong, ulong> channel = new();               // native +0x380 while playing: changes per sound played
    // other players' footstep sources: GameObject, last step, last position sent (Unity metres)
    readonly Dictionary<ulong, (ulong g, DateTime last, double x, double y, double z)> follow = new();
    const double FollowSec = 3;                                      // keep moving the mark this long after a step
    readonly Dictionary<ulong, List<string>> parentsOf = new();
    double[]? lastL;
    (double x, double y, double z) you;                       // listener position (Unity metres)

    public PhasmoFeed(Mem mem, string path, TextWriter logTo)
    {
        m = mem; log = logTo;
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        w = new StreamWriter(fs, new UTF8Encoding(false));
    }

    public void Close() => w.Dispose();

    // UnityEngine.<name> Il2CppClass: its name string, then the class whose name field points at it
    void FindClass(string name, HashSet<ulong> into)
    {
        var strs = m.FindBytes(Encoding.ASCII.GetBytes(name + "\0"), 200);
        foreach (var (at, _) in m.FindAny(new HashSet<ulong>(strs), 5000))
        {
            ulong k = at - KName;
            if (m.Ptr(k + KSelf) == k && m.CStr(m.Ptr(k + KNs)) == "UnityEngine") into.Add(k);
        }
    }

    public bool FindClasses()
    {
        FindClass("AudioSource", sourceCls);
        FindClass("AudioListener", listenerCls);
        log.WriteLine($"  AudioSource class {sourceCls.Count}, AudioListener class {listenerCls.Count}");
        return sourceCls.Count > 0 && listenerCls.Count > 0;
    }

    // all AudioSources + the enabled listener (heap scan, a few seconds; again every 20 s for new objects)
    public void Scan()
    {
        natives = m.FindAny(sourceCls, 20000).Select(x => m.Ptr(x.at + 0x10)).Where(n => n != 0).Distinct().ToList();
        ulong best = 0;
        foreach (var (o, _) in m.FindAny(listenerCls, 1000))
        {
            ulong n = m.Ptr(o + 0x10);
            if (n == 0 || m.Read<byte>(n + 0x39) == 0) continue;      // +0x38 enabled, +0x39 active (VR rig = 0)
            best = n;
            if (Parents(m.Ptr(n + 0x30)).Any(p => p.StartsWith("LocalPCPlayer"))) break;
        }
        listener = best;
    }

    public int Count => natives.Count;

    string GoName(ulong g)
    {
        string s = m.CStr(m.Ptr(g + 0x60), 64);
        return s.All(c => c >= 32 && c < 127) ? s : "";
    }

    // up to 5 parent GameObject names, nearest first
    List<string> Parents(ulong g)
    {
        var names = new List<string>();
        ulong tr = m.Ptr(m.Ptr(g + 0x30) + 8), hier = m.Ptr(tr + 0x38);
        int idx = m.I32(tr + 0x40);
        ulong par = m.Ptr(hier + 0x20), arr = m.Ptr(hier + 0x30);
        if (hier == 0 || par == 0 || idx < 0) return names;
        int pi = m.I32(par + (ulong)idx * 4);
        for (int d = 0; pi >= 0 && d < 5; d++)
        {
            names.Add(GoName(m.Ptr(m.Ptr(arr + (ulong)pi * 8) + 0x30)));
            pi = m.I32(par + (ulong)pi * 4);
        }
        return names;
    }

    // world position + rotation of a GameObject (walks the hierarchy: parent TRS applied to the child)
    (double x, double y, double z, double qx, double qy, double qz, double qw)? World(ulong g)
    {
        ulong tr = m.Ptr(m.Ptr(g + 0x30) + 8), hier = m.Ptr(tr + 0x38);
        int idx = m.I32(tr + 0x40);
        ulong loc = m.Ptr(hier + 0x18), par = m.Ptr(hier + 0x20);
        if (loc == 0 || par == 0 || idx < 0) return null;
        var t0 = m.Bytes(loc + (ulong)idx * 0x30, 0x30); if (t0.Length == 0) return null;
        double x = BitConverter.ToSingle(t0, 0), y = BitConverter.ToSingle(t0, 4), z = BitConverter.ToSingle(t0, 8);
        double rx = BitConverter.ToSingle(t0, 16), ry = BitConverter.ToSingle(t0, 20), rz = BitConverter.ToSingle(t0, 24), rw = BitConverter.ToSingle(t0, 28);
        int p = m.I32(par + (ulong)idx * 4);
        for (int d = 0; p >= 0 && d < 64; d++)
        {
            var n = m.Bytes(loc + (ulong)p * 0x30, 0x30); if (n.Length == 0) return null;
            double qx = BitConverter.ToSingle(n, 16), qy = BitConverter.ToSingle(n, 20), qz = BitConverter.ToSingle(n, 24), qw = BitConverter.ToSingle(n, 28);
            x *= BitConverter.ToSingle(n, 32); y *= BitConverter.ToSingle(n, 36); z *= BitConverter.ToSingle(n, 40);
            (x, y, z) = Rotate(qx, qy, qz, qw, x, y, z);
            x += BitConverter.ToSingle(n, 0); y += BitConverter.ToSingle(n, 4); z += BitConverter.ToSingle(n, 8);
            // child rotation becomes parent * child
            (rx, ry, rz, rw) = (qw * rx + qx * rw + qy * rz - qz * ry, qw * ry - qx * rz + qy * rw + qz * rx,
                                qw * rz + qx * ry - qy * rx + qz * rw, qw * rw - qx * rx - qy * ry - qz * rz);
            p = m.I32(par + (ulong)p * 4);
        }
        return (x, y, z, rx, ry, rz, rw);
    }

    static (double, double, double) Rotate(double qx, double qy, double qz, double qw, double x, double y, double z)
    {
        double cx = qy * z - qz * y, cy = qz * x - qx * z, cz = qx * y - qy * x;
        double ccx = qy * cz - qz * cy, ccy = qz * cx - qx * cz, ccz = qx * cy - qy * cx;
        return (x + 2 * (qw * cx + ccx), y + 2 * (qw * cy + ccy), z + 2 * (qw * cz + ccz));
    }

    static string F(double v) => v.ToString("0", CultureInfo.InvariantCulture);
    static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace(' ', '_');
    void Emit(string line) => w.WriteLine(line);

    // Unity (metres, Y up, Z forward, X right) -> the feed's Unreal convention (cm, X forward, Y right, Z up)
    static (double, double, double) ToFeed(double x, double y, double z) => (z * 100, x * 100, y * 100);

    // what a sound is, from its GameObject name and parents (log of a real round, 2026-10-17)
    (string name, string src, bool flat) Classify(string go, List<string> ps, double x, double y, double z)
    {
        string p0 = ps.Count > 0 ? ps[0] : "", all = string.Join("<", ps);
        bool nearYou = Math.Abs(x - you.x) + Math.Abs(z - you.z) < 1.2;
        // ghost
        if (p0.StartsWith("Footstep") && ps.Count > 1 && ps[1] == "Footsteps") return ("Footstep_Ghost", "ghost", false);
        if (all.Contains("Ghost_Main")) return (go.StartsWith("Loop") ? "Ghost_Appears" : "Ghost_Sound", "ghost", false);
        if (p0 == "GhostWhisper") return ("GhostWhisper", "ghost", false);
        if (p0.StartsWith("GhostEventPlayer")) return ("Ghost_Event", "ghost", false);
        if (go.StartsWith("HeartbeatSound")) return ("Heartbeat", "ghost", true);
        if (go.StartsWith("EVPAudioSound")) return ("SpiritBox_Answer", "ghost", false);
        // players
        if (p0 == "FootstepAudio") return ("Footsteps_Player", nearYou ? "own" : "player", false);
        if (all.Contains("PCPlayer")) return ("Player_" + Clean(go), nearYou ? "own" : "player", false);
        // doors
        if (p0.StartsWith("Door_") || p0 == "Door")
            return (go.StartsWith("impact") ? "Door_Bang" : go.Contains("Loop") ? "Door_Creak" : "Door_" + Clean(go), "other", false);
        // equipment ("EMF Reader I", "Spiritbox I" ...)
        string item = ps.FirstOrDefault(p => p.EndsWith(" I")) ?? (go.EndsWith(" I") ? go : "");
        if (item != "" || go.StartsWith("EVPAudio") || go.StartsWith("Sound sensor") || go.StartsWith("Parabolic"))
        {
            string it = item != "" ? item[..^2] : go;
            bool held = all.Contains("CC_Base_") && nearYou;
            return (it.Contains("Spirit") ? "SpiritBox" : Clean(it), held ? "own" : "other", false);
        }
        if (p0.StartsWith("LightSwitch")) return ("lightswitch", "other", false);
        if (p0 == "Radio" || all.Contains("Radio") && !all.Contains("Truck")) return ("Radio", "other", false);
        // always-on beds, truck, menus, house noise spots: Ambient_* (hidden by the Phasmo config)
        if (all.Contains("Viewport") || all.Contains("Canvas") || all.Contains("Scroll View") || all.Contains("Settings") ||
            go.StartsWith("Noise Spot") || go.Contains("Rain") || go.Contains("Ambien") || go.StartsWith("Floor Vent") ||
            go.StartsWith("Moon Altar") || all.Contains("Truck") || all.Contains("AUDIO") || all.Contains("Systems") ||
            go is "volume" or "pitch" or "LOD1" or "Menu Audio" or "Buttons Audio" or "Elevator02" || go.StartsWith("Truck"))
            return ("Ambient_" + Clean(go), "other", false);
        // anything else making a noise in the house = an object moved / knocked / thrown
        if (go.Contains("Noise Cube") && p0 != "") return ("Impact_" + Clean(p0), "other", false);
        return (Clean(go == "" ? "Unknown" : go) + (p0 != "" ? "_" + Clean(p0) : ""), "other", false);
    }

    public bool Tick()
    {
        if (listener != 0)
        {
            var l = World(m.Ptr(listener + 0x30));
            if (l != null)
            {
                var v = l.Value;
                you = (v.x, v.y, v.z);
                var (fx, _, fz) = Rotate(v.qx, v.qy, v.qz, v.qw, 0, 0, 1);
                double yaw = Math.Atan2(fx, fz) * 180 / Math.PI;
                var (ux, uy, uz) = ToFeed(v.x, v.y, v.z);
                if (lastL == null || Math.Abs(ux - lastL[0]) + Math.Abs(uy - lastL[1]) + Math.Abs(uz - lastL[2]) > 5 || Math.Abs(yaw - lastL[3]) > 1)
                {
                    lastL = [ux, uy, uz, yaw];
                    Emit($"L\t{F(ux)}\t{F(uy)}\t{F(uz)}\t{F1(yaw)}");
                }
            }
        }
        foreach (var n in natives)
        {
            ulong ch = m.Ptr(n + 0x380);
            if (ch == 0)
            {
                channel.Remove(n);
                if (playing.Remove(n)) Emit($"E\t{n}");
                continue;
            }
            // a new channel on a source that is still playing = the next sound started before the last one ended
            // (a player's footsteps when walking fast: one source, one step after another)
            bool again = channel.TryGetValue(n, out var was) && was != ch;
            channel[n] = ch;
            if (playing.ContainsKey(n) && !again) continue;
            ulong g = m.Ptr(n + 0x30);
            string go = GoName(g);
            if (go == "") { playing[n] = ("", ""); continue; }
            if (!parentsOf.TryGetValue(g, out var ps)) parentsOf[g] = ps = Parents(g);
            var wpos = World(g);
            double x = wpos?.x ?? 0, y = wpos?.y ?? 0, z = wpos?.z ?? 0;
            var (name, src, flat) = Classify(go, ps, x, y, z);
            playing[n] = (name, src);
            if (name == "Footsteps_Player" && src == "player") follow[n] = (g, DateTime.UtcNow, x, y, z);
            if (flat || wpos == null) Emit($"2\t{name}\t{src}");
            else
            {
                var (fx, fy, fz) = ToFeed(x, y, z);
                Emit($"S\t{n}\t{name}\t{F(fx)}\t{F(fy)}\t{F(fz)}\t-1\t{src}");
            }
            if (Verbose && src != "own" && !name.StartsWith("Ambient_"))
                log.WriteLine($"{DateTime.Now:HH:mm:ss} {name} ({src}): {go} < {string.Join(" < ", ps)}");
        }
        // other players' steps: the mark follows the player between steps (Kaalob: where they are now, not where they were)
        foreach (var (n, f) in follow.ToList())
        {
            if ((DateTime.UtcNow - f.last).TotalSeconds > FollowSec) { follow.Remove(n); continue; }
            var p = World(f.g);
            if (p == null) continue;
            var (x, y, z) = (p.Value.x, p.Value.y, p.Value.z);
            if (Math.Abs(x - f.x) + Math.Abs(y - f.y) + Math.Abs(z - f.z) < 0.2) continue;
            follow[n] = (f.g, f.last, x, y, z);
            var (fx, fy, fz) = ToFeed(x, y, z);
            Emit($"M\t{n}\t{F(fx)}\t{F(fy)}\t{F(fz)}");
        }
        w.Flush();
        return true;
    }

    public bool Verbose = true;
}
