using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhasmoReader;

// Read-only view of another process. The handle is opened with VM_READ + QUERY_INFORMATION
// only, so Windows itself refuses any write: nothing here can change the game.
sealed class Mem : IDisposable
{
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;     // for VirtualQueryEx (lists memory regions, still read-only)

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, nint size, out nint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern unsafe bool ReadProcessMemory(IntPtr h, IntPtr addr, void* buf, nint size, out nint read);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] mods, int cb, out int needed, uint filter);
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetModuleInformation(IntPtr h, IntPtr mod, out MODULEINFO info, int cb);
    [StructLayout(LayoutKind.Sequential)]
    struct MODULEINFO { public IntPtr Base; public int Size; public IntPtr Entry; }
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    static extern int GetModuleBaseNameW(IntPtr h, IntPtr mod, StringBuilder name, int size);

    // A loaded module (e.g. GameAssembly.dll, where IL2CPP keeps the game code): base + size, or (0, 0).
    public (ulong b, int size) Module(string name)
    {
        var mods = new IntPtr[1024];
        if (!EnumProcessModulesEx(h, mods, mods.Length * IntPtr.Size, out int needed, 0x03)) return (0, 0);
        var sb = new StringBuilder(260);
        for (int i = 0; i < Math.Min(mods.Length, needed / IntPtr.Size); i++)
        {
            sb.Clear();
            GetModuleBaseNameW(h, mods[i], sb, sb.Capacity);
            if (!sb.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            GetModuleInformation(h, mods[i], out var info, Marshal.SizeOf<MODULEINFO>());
            return ((ulong)info.Base, info.Size);
        }
        return (0, 0);
    }

    readonly IntPtr h;
    public readonly int Pid;
    public readonly ulong Base;
    public readonly int Size;

    Mem(int pid, IntPtr handle, ulong b, int size) { Pid = pid; h = handle; Base = b; Size = size; }

    public static Mem? Open(string processName)
    {
        var p = Process.GetProcessesByName(processName).FirstOrDefault();
        if (p == null) return null;
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess failed, error " + Marshal.GetLastWin32Error());
        // First module of the process = the game exe.
        var mods = new IntPtr[1];
        if (!EnumProcessModulesEx(h, mods, IntPtr.Size, out _, 0x03))
            throw new Exception("EnumProcessModulesEx failed, error " + Marshal.GetLastWin32Error());
        GetModuleInformation(h, mods[0], out var info, Marshal.SizeOf<MODULEINFO>());
        return new Mem(p.Id, h, (ulong)info.Base, info.Size);
    }

    public bool Alive
    {
        get { try { return !Process.GetProcessById(Pid).HasExited; } catch (ArgumentException) { return false; } }
    }

    public byte[] Bytes(ulong addr, int n)
    {
        var b = new byte[n];
        if (addr == 0 || !ReadProcessMemory(h, (IntPtr)(long)addr, b, n, out var got) || got != n)
            return Array.Empty<byte>();
        return b;
    }

    public unsafe T Read<T>(ulong addr) where T : unmanaged
    {
        T v = default;
        if (addr != 0) ReadProcessMemory(h, (IntPtr)(long)addr, &v, sizeof(T), out _);
        return v;
    }

    public ulong Ptr(ulong addr) => Read<ulong>(addr);
    public int I32(ulong addr) => Read<int>(addr);
    public uint U32(ulong addr) => Read<uint>(addr);
    public float F32(ulong addr) => Read<float>(addr);
    public double F64(ulong addr) => Read<double>(addr);

    // Reads the module image in pages; unreadable pages stay zero.
    public byte[] Image()
    {
        var img = new byte[Size];
        const int page = 0x1000;
        var buf = new byte[page];
        for (int off = 0; off < Size; off += page)
        {
            int n = Math.Min(page, Size - off);
            if (ReadProcessMemory(h, (IntPtr)(long)(Base + (ulong)off), buf, n, out var got) && got == n)
                Buffer.BlockCopy(buf, 0, img, off, n);
        }
        return img;
    }

    [DllImport("kernel32.dll")]
    static extern nint VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION info, nint len);
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public ulong BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId;
        public ulong RegionSize; public uint State, Protect, Type;
    }

    // Every committed readable region (heap, mapped files, images): for finding strings.
    public IEnumerable<(ulong start, ulong size)> ReadableRegions()
    {
        ulong a = 0x10000;
        while (a < 0x7FFFFFFF0000 && VirtualQueryEx(h, (IntPtr)(long)a, out var mi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
        {
            if (mi.State == 0x1000 && (mi.Protect & 0x66) != 0 && (mi.Protect & 0x101) == 0)
                yield return (mi.BaseAddress, mi.RegionSize);
            a = mi.BaseAddress + Math.Max(mi.RegionSize, 0x1000);
        }
    }

    // Committed, private, read-write regions (the game's heap).
    public IEnumerable<(ulong start, ulong size)> HeapRegions()
    {
        ulong a = 0x10000;
        while (a < 0x7FF000000000 && VirtualQueryEx(h, (IntPtr)(long)a, out var mi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
        {
            if (mi.State == 0x1000 && mi.Type == 0x20000 && (mi.Protect & 0x04) != 0 && (mi.Protect & 0x100) == 0)
                yield return (mi.BaseAddress, mi.RegionSize);
            a = mi.BaseAddress + Math.Max(mi.RegionSize, 0x1000);
        }
    }

    // Addresses in the heap that hold the 8-byte value v.
    public List<ulong> FindQword(ulong v, int max = 1000) => FindAny([v], max).Select(x => x.at).ToList();

    // Addresses in the heap that hold any of the values (one pass over the heap).
    public List<(ulong at, ulong value)> FindAny(HashSet<ulong> values, int max = 5000, IEnumerable<(ulong start, ulong size)>? regions = null)
    {
        var hits = new List<(ulong at, ulong value)>();
        if (values.Count == 0) return hits;
        // quick pre-check on 16 bits of the value before the HashSet (most heap words fail here)
        var maybe = new bool[1 << 16];
        foreach (var v in values) maybe[(int)((v >> 3) & 0xFFFF)] = true;
        var chunks = new List<(ulong at, int n)>();
        foreach (var (start, size) in regions ?? HeapRegions())
            for (ulong off = 0; off < size; off += 0x400000)
                chunks.Add((start + off, (int)Math.Min(0x400000, size - off)));
        int count = 0;
        // one pass over the heap, split over the CPU cores (still only reads)
        Parallel.ForEach(chunks, () => new List<(ulong, ulong)>(), (c, state, mine) =>
        {
            if (Volatile.Read(ref count) >= max) { state.Stop(); return mine; }
            void Scan(ulong at, byte[] b)
            {
                var span = MemoryMarshal.Cast<byte, ulong>(b);
                for (int i = 0; i < span.Length; i++)
                {
                    ulong v = span[i];
                    if (maybe[(int)((v >> 3) & 0xFFFF)] && values.Contains(v)) { mine.Add((at + (ulong)i * 8, v)); Interlocked.Increment(ref count); }
                }
            }
            var b = Bytes(c.at, c.n);
            if (b.Length != 0) { Scan(c.at, b); return mine; }
            // the big read failed (a page changed or went away while reading): read this chunk in 64 KB pieces
            for (int sub = 0; sub < c.n; sub += 0x10000)
            {
                var p = Bytes(c.at + (ulong)sub, Math.Min(0x10000, c.n - sub));
                if (p.Length != 0) Scan(c.at + (ulong)sub, p);
            }
            return mine;
        }, mine => { lock (hits) hits.AddRange(mine); });
        hits.Sort((a, b) => a.at.CompareTo(b.at));
        return hits.Count > max ? hits.GetRange(0, max) : hits;
    }

    public string Ascii(ulong addr, int n) => Encoding.ASCII.GetString(Bytes(addr, n));

    // Zero-terminated UTF-8 string (IL2CPP names), up to max bytes.
    public string CStr(ulong addr, int max = 128)
    {
        var b = Bytes(addr, max);
        if (b.Length == 0) { b = Bytes(addr, 16); if (b.Length == 0) return ""; }
        int n = Array.IndexOf(b, (byte)0);
        return Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n);
    }

    // Addresses where these bytes appear (any readable memory).
    public List<ulong> FindBytes(byte[] pat, int max = 100)
    {
        var hits = new List<ulong>();
        foreach (var (start, size) in ReadableRegions())
            for (ulong off = 0; off < size && hits.Count < max; off += 0x400000)
            {
                int n = (int)Math.Min((ulong)(0x400000 + pat.Length), size - off);
                var b = Bytes(start + off, n);
                if (b.Length == 0) continue;
                for (int i = b.AsSpan().IndexOf(pat); i >= 0; )
                {
                    hits.Add(start + off + (ulong)i);
                    int j = b.AsSpan(i + 1).IndexOf(pat);
                    i = j < 0 ? -1 : i + 1 + j;
                }
            }
        return hits.Distinct().ToList();
    }

    public void Dispose() => CloseHandle(h);
}
