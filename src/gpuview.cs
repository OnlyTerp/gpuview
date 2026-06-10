// gpuview — GPU-native screen capture via DXGI Desktop Duplication.
// The compositor hands us the exact surface it scans out (zero GDI), plus
// dirty-rect metadata telling us WHAT changed between frames.
// Modes:
//   frame [out.png]                  one frame, full
//   watch <seconds> <outdir>         poll; emit only changed-region crops + JSON events
//   diff <outdir>                    two frames; emit changed regions only
// Output: JSON lines on stdout (machine-readable), PNGs on disk.
//
// Build: csc /target:exe /platform:x64 /out:gpuview.exe gpuview.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Native
{
    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr pAdapter, int DriverType, IntPtr Software,
        uint Flags, IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("dxgi.dll")]
    public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
}

// ---- minimal COM vtable helpers -------------------------------------------------
static class Com
{
    public delegate int QueryInterfaceFn(IntPtr self, ref Guid riid, out IntPtr ppv);
    public static IntPtr Get(IntPtr self, ref Guid iid)
    {
        var qi = GetFn<QueryInterfaceFn>(self, 0);
        IntPtr o; int hr = qi(self, ref iid, out o);
        if (hr != 0) throw new COMException("QI failed", hr);
        return o;
    }
    public static T GetFn<T>(IntPtr comObj, int slot)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(comObj);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }
    public static void Release(IntPtr o) { if (o != IntPtr.Zero) Marshal.Release(o); }
}

[StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
[StructLayout(LayoutKind.Sequential)]
struct DXGI_OUTDUPL_FRAME_INFO
{
    public long LastPresentTime, LastMouseUpdateTime;
    public uint AccumulatedFrames; public int RectsCoalesced, ProtectedContentMaskedOut;
    public POINTERPOS PointerPosition; public uint TotalMetadataBufferSize, PointerShapeBufferSize;
}
[StructLayout(LayoutKind.Sequential)] struct POINTERPOS { public int x, y; public int Visible; }
[StructLayout(LayoutKind.Sequential)]
struct D3D11_TEXTURE2D_DESC
{
    public uint Width, Height, MipLevels, ArraySize; public int Format;
    public uint SampleCount, SampleQuality; public int Usage; public uint BindFlags, CPUAccessFlags, MiscFlags;
}
[StructLayout(LayoutKind.Sequential)] struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }

class Duplicator : IDisposable
{
    // vtable slots (from headers):
    // IDXGIFactory1: EnumAdapters1 = 12
    // IDXGIAdapter:  EnumOutputs = 7
    // IDXGIOutput1:  DuplicateOutput = 22
    // IDXGIOutputDuplication: AcquireNextFrame=8, GetFrameDirtyRects=9, ReleaseFrame=14
    // ID3D11Device:  CreateTexture2D = 5
    // ID3D11DeviceContext: Map=14, Unmap=15, CopyResource=47
    // ID3D11Texture2D: GetDesc = 10
    delegate int EnumAdapters1Fn(IntPtr f, uint i, out IntPtr a);
    delegate int EnumOutputsFn(IntPtr a, uint i, out IntPtr o);
    delegate int DuplicateOutputFn(IntPtr o1, IntPtr dev, out IntPtr dup);
    delegate int AcquireNextFrameFn(IntPtr dup, uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource);
    delegate int GetFrameDirtyRectsFn(IntPtr dup, uint sz, IntPtr buf, out uint required);
    delegate int ReleaseFrameFn(IntPtr dup);
    delegate int CreateTexture2DFn(IntPtr dev, ref D3D11_TEXTURE2D_DESC d, IntPtr init, out IntPtr tex);
    delegate int MapFn(IntPtr ctx, IntPtr res, uint sub, int type, uint flags, out D3D11_MAPPED_SUBRESOURCE m);
    delegate void UnmapFn(IntPtr ctx, IntPtr res, uint sub);
    delegate void CopyResourceFn(IntPtr ctx, IntPtr dst, IntPtr src);
    delegate void GetDescFn(IntPtr tex, out D3D11_TEXTURE2D_DESC d);

    IntPtr device, context, dup, staging;
    public int W, H;
    public int Fmt;   // DXGI_FORMAT of the desktop surface (87=BGRA8, 10=RGBA16F/HDR)

    static Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
    static Guid IID_IDXGIOutput1  = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
    static Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public Duplicator(uint outputIndex = 0)
    {
        IntPtr factory; int hr = Native.CreateDXGIFactory1(ref IID_IDXGIFactory1, out factory);
        if (hr != 0) throw new COMException("CreateDXGIFactory1", hr);
        IntPtr adapter; Com.GetFn<EnumAdapters1Fn>(factory, 12)(factory, 0, out adapter);
        IntPtr output;
        hr = Com.GetFn<EnumOutputsFn>(adapter, 7)(adapter, outputIndex, out output);
        if (hr != 0) throw new COMException("EnumOutputs(" + outputIndex + ")", hr);
        IntPtr output1 = Com.Get(output, ref IID_IDXGIOutput1);

        int fl;
        hr = Native.D3D11CreateDevice(adapter, /*D3D_DRIVER_TYPE_UNKNOWN*/0, IntPtr.Zero, 0,
            IntPtr.Zero, 0, 7 /*D3D11_SDK_VERSION*/, out device, out fl, out context);
        if (hr != 0) throw new COMException("D3D11CreateDevice", hr);

        hr = Com.GetFn<DuplicateOutputFn>(output1, 22)(output1, device, out dup);
        if (hr != 0) throw new COMException("DuplicateOutput (is a fullscreen-exclusive app or RDP blocking?)", hr);

        Com.Release(output1); Com.Release(output); Com.Release(adapter); Com.Release(factory);
    }

    void EnsureStaging(IntPtr gpuTex)
    {
        D3D11_TEXTURE2D_DESC d; Com.GetFn<GetDescFn>(gpuTex, 10)(gpuTex, out d);
        W = (int)d.Width; H = (int)d.Height; Fmt = d.Format;
        if (staging != IntPtr.Zero) return;
        var sd = d;
        sd.Usage = 3;            // D3D11_USAGE_STAGING
        sd.BindFlags = 0; sd.CPUAccessFlags = 0x20000; // READ
        sd.MiscFlags = 0; sd.MipLevels = 1; sd.ArraySize = 1; sd.SampleCount = 1; sd.SampleQuality = 0;
        int hr = Com.GetFn<CreateTexture2DFn>(device, 5)(device, ref sd, IntPtr.Zero, out staging);
        if (hr != 0) throw new COMException("CreateTexture2D staging", hr);
    }

    /// Acquire one frame. Returns bitmap + dirty rects (empty list = nothing changed).
    public Bitmap Acquire(uint timeoutMs, out List<RECT> dirty, out bool timedOut)
    {
        dirty = new List<RECT>(); timedOut = false;
        DXGI_OUTDUPL_FRAME_INFO info; IntPtr res;
        int hr = Com.GetFn<AcquireNextFrameFn>(dup, 8)(dup, timeoutMs, out info, out res);
        if (hr == unchecked((int)0x887A0027)) { timedOut = true; return null; } // DXGI_ERROR_WAIT_TIMEOUT
        if (hr != 0) throw new COMException("AcquireNextFrame", hr);
        try
        {
            IntPtr tex = Com.Get(res, ref IID_ID3D11Texture2D);
            EnsureStaging(tex);
            Com.GetFn<CopyResourceFn>(context, 47)(context, staging, tex);
            Com.Release(tex);

            if (info.TotalMetadataBufferSize > 0)
            {
                uint need;
                IntPtr buf = Marshal.AllocHGlobal((int)info.TotalMetadataBufferSize);
                try
                {
                    hr = Com.GetFn<GetFrameDirtyRectsFn>(dup, 9)(dup, info.TotalMetadataBufferSize, buf, out need);
                    if (hr == 0)
                    {
                        int n = (int)(need / Marshal.SizeOf(typeof(RECT)));
                        for (int i = 0; i < n; i++)
                            dirty.Add(Marshal.PtrToStructure<RECT>(buf + i * Marshal.SizeOf(typeof(RECT))));
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            D3D11_MAPPED_SUBRESOURCE map;
            hr = Com.GetFn<MapFn>(context, 14)(context, staging, 0, 1 /*READ*/, 0, out map);
            if (hr != 0) throw new COMException("Map", hr);
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            unsafe
            {
                if (Fmt == 10) // DXGI_FORMAT_R16G16B16A16_FLOAT (HDR/scRGB desktop)
                {
                    for (int y = 0; y < H; y++)
                    {
                        ushort* src = (ushort*)(map.pData + y * (int)map.RowPitch);
                        byte* dst = (byte*)(bd.Scan0 + y * bd.Stride);
                        for (int x = 0; x < W; x++)
                        {
                            // half-float RGBA (scRGB linear) -> 8-bit sRGB BGRA
                            float r = HalfToFloat(src[x * 4 + 0]);
                            float g = HalfToFloat(src[x * 4 + 1]);
                            float b = HalfToFloat(src[x * 4 + 2]);
                            dst[x * 4 + 0] = LinearToSrgb8(b);
                            dst[x * 4 + 1] = LinearToSrgb8(g);
                            dst[x * 4 + 2] = LinearToSrgb8(r);
                            dst[x * 4 + 3] = 255;
                        }
                    }
                }
                else // 87/88 = BGRA8 and friends: straight row copy
                {
                    for (int y = 0; y < H; y++)
                    {
                        Buffer.MemoryCopy((void*)(map.pData + y * (int)map.RowPitch),
                                          (void*)(bd.Scan0 + y * bd.Stride), bd.Stride, W * 4);
                    }
                }
            }
            bmp.UnlockBits(bd);
            Com.GetFn<UnmapFn>(context, 15)(context, staging, 0);
            return bmp;
        }
        finally
        {
            Com.GetFn<ReleaseFrameFn>(dup, 14)(dup);
            Com.Release(res);
        }
    }

    static float HalfToFloat(ushort h)
    {
        int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
        if (exp == 0) { if (mant == 0) return sign == 1 ? -0f : 0f; // subnormal
                        float v = mant * (1f / 1024f) * (float)Math.Pow(2, -14); return sign == 1 ? -v : v; }
        if (exp == 31) return sign == 1 ? float.NegativeInfinity : float.PositiveInfinity;
        float f = (1f + mant * (1f / 1024f)) * (float)Math.Pow(2, exp - 15);
        return sign == 1 ? -f : f;
    }

    static byte LinearToSrgb8(float v)
    {
        if (v <= 0f) return 0;
        if (v > 1f) v = 1f;   // simple clamp tonemap; SDR content in scRGB lands 0..1
        float s = v <= 0.0031308f ? v * 12.92f : 1.055f * (float)Math.Pow(v, 1.0 / 2.4) - 0.055f;
        int i = (int)(s * 255f + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    public void Dispose()
    {
        Com.Release(staging); Com.Release(dup); Com.Release(context); Com.Release(device);
    }
}

static class Program
{
    static string J(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }

    static List<RECT> Merge(List<RECT> rects, int pad, int W, int H)
    {
        // pad + merge overlapping rects into coarse regions
        var rs = new List<RECT>();
        foreach (var r in rects)
        {
            var e = new RECT {
                l = Math.Max(0, r.l - pad), t = Math.Max(0, r.t - pad),
                r = Math.Min(W, r.r + pad), b = Math.Min(H, r.b + pad) };
            bool merged = false;
            for (int i = 0; i < rs.Count; i++)
            {
                var x = rs[i];
                if (e.l < x.r && x.l < e.r && e.t < x.b && x.t < e.b)
                {
                    rs[i] = new RECT { l = Math.Min(x.l, e.l), t = Math.Min(x.t, e.t),
                                       r = Math.Max(x.r, e.r), b = Math.Max(x.b, e.b) };
                    merged = true; break;
                }
            }
            if (!merged) rs.Add(e);
        }
        return rs;
    }

    static void SaveCrop(Bitmap full, RECT r, string path)
    {
        int w = r.r - r.l, h = r.b - r.t;
        using (var c = full.Clone(new Rectangle(r.l, r.t, w, h), full.PixelFormat))
            c.Save(path, ImageFormat.Png);
    }

    static int Area(RECT r)
    {
        return Math.Max(0, r.r - r.l) * Math.Max(0, r.b - r.t);
    }

    static long SumArea(List<RECT> rects)
    {
        long total = 0;
        foreach (var r in rects) total += Area(r);
        return total;
    }

    static double NonBlackFraction(Bitmap bmp)
    {
        int W = bmp.Width, H = bmp.Height;
        int sx = Math.Max(1, W / 320), sy = Math.Max(1, H / 180);
        long sampled = 0, nonblack = 0;
        var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < H; y += sy)
                {
                    byte* p = (byte*)bd.Scan0 + y * bd.Stride;
                    for (int x = 0; x < W; x += sx)
                    {
                        byte* px = p + x * 4;
                        sampled++;
                        if (px[0] > 2 || px[1] > 2 || px[2] > 2) nonblack++;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return sampled == 0 ? 0 : (double)nonblack / sampled;
    }

    static Bitmap CaptureGdi(uint outIdx, out Rectangle bounds)
    {
        var screens = Screen.AllScreens;
        int idx = outIdx < screens.Length ? (int)outIdx : 0;
        bounds = screens[idx].Bounds;
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    static List<RECT> Refine(Bitmap prev, Bitmap cur, List<RECT> hints, int minArea, int pad)
    {
        int W = cur.Width, H = cur.Height, tile = 32;
        if (prev == null || prev.Width != W || prev.Height != H) return Merge(hints, pad, W, H);

        var hs = hints.Count == 0
            ? new List<RECT> { new RECT { l = 0, t = 0, r = W, b = H } }
            : Merge(hints, 0, W, H);
        int gw = (W + tile - 1) / tile, gh = (H + tile - 1) / tile;
        var dirtyTiles = new bool[gw * gh];
        var bounds = new Rectangle(0, 0, W, H);
        var pbd = prev.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var cbd = cur.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            unsafe
            {
                foreach (var raw in hs)
                {
                    var h = new RECT { l = Math.Max(0, raw.l), t = Math.Max(0, raw.t), r = Math.Min(W, raw.r), b = Math.Min(H, raw.b) };
                    if (h.r <= h.l || h.b <= h.t) continue;
                    int tx0 = h.l / tile, ty0 = h.t / tile;
                    int tx1 = (h.r - 1) / tile, ty1 = (h.b - 1) / tile;
                    for (int ty = ty0; ty <= ty1; ty++)
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        int x0 = Math.Max(h.l, tx * tile), y0 = Math.Max(h.t, ty * tile);
                        int x1 = Math.Min(h.r, (tx + 1) * tile), y1 = Math.Min(h.b, (ty + 1) * tile);
                        int changed = 0; bool found = false;
                        for (int y = y0; y < y1 && !found; y++)
                        {
                            byte* p = (byte*)pbd.Scan0 + y * pbd.Stride + x0 * 4;
                            byte* c = (byte*)cbd.Scan0 + y * cbd.Stride + x0 * 4;
                            for (int x = x0; x < x1; x++, p += 4, c += 4)
                            {
                                int d = Math.Abs(p[0] - c[0]) + Math.Abs(p[1] - c[1]) + Math.Abs(p[2] - c[2]);
                                if (d > 45 && ++changed >= 8) { found = true; break; }
                            }
                        }
                        if (found) dirtyTiles[ty * gw + tx] = true;
                    }
                }
            }
        }
        finally
        {
            prev.UnlockBits(pbd);
            cur.UnlockBits(cbd);
        }

        var seen = new bool[dirtyTiles.Length];
        var boxes = new List<RECT>();
        int[] qx = new int[dirtyTiles.Length], qy = new int[dirtyTiles.Length];
        int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
        for (int y = 0; y < gh; y++)
        for (int x = 0; x < gw; x++)
        {
            int start = y * gw + x;
            if (!dirtyTiles[start] || seen[start]) continue;
            int head = 0, tail = 0, minX = x, maxX = x, minY = y, maxY = y;
            qx[tail] = x; qy[tail++] = y; seen[start] = true;
            while (head < tail)
            {
                int cx = qx[head], cy = qy[head++];
                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i], ny = cy + dy[i];
                    if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                    int ni = ny * gw + nx;
                    if (!dirtyTiles[ni] || seen[ni]) continue;
                    seen[ni] = true; qx[tail] = nx; qy[tail++] = ny;
                }
            }
            var box = new RECT { l = minX * tile, t = minY * tile, r = Math.Min(W, (maxX + 1) * tile), b = Math.Min(H, (maxY + 1) * tile) };
            if (Area(box) >= minArea) boxes.Add(box);
        }

        var refined = Merge(boxes, pad, W, H);
        var keep = new List<RECT>();
        foreach (var r in refined) if (Area(r) >= minArea) keep.Add(r);
        return keep;
    }

    static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext((IntPtr)(-4)); // PerMonitorV2
        string mode = args.Length > 0 ? args[0] : "frame";
        uint outIdx = 0;
        // optional trailing arg: monitor/output index
        if (args.Length > 0 && args[args.Length - 1].StartsWith("mon="))
            outIdx = uint.Parse(args[args.Length - 1].Substring(4));
        try
        {
            using (var dup = new Duplicator(outIdx))
            {
                if (mode == "frame")
                {
                    string outPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "gpuview_frame.png");
                    Bitmap bmp = null; List<RECT> dirty; bool to; double nb = 0; string source = "dxgi";
                    for (int i = 0; i < 20; i++)
                    {
                        var candidate = dup.Acquire(250, out dirty, out to);
                        if (candidate == null) continue;
                        nb = NonBlackFraction(candidate);
                        if (nb > 0.001)
                        {
                            bmp = candidate;
                            break;
                        }
                        candidate.Dispose();
                    }
                    if (bmp == null)
                    {
                        Rectangle b;
                        bmp = CaptureGdi(outIdx, out b);
                        nb = NonBlackFraction(bmp);
                        source = "gdi_fallback";
                    }
                    if (bmp == null) { Console.WriteLine("{\"event\":\"error\",\"msg\":\"no frame\"}"); return 2; }
                    bmp.Save(outPath, ImageFormat.Png);
                    Console.WriteLine("{\"event\":\"frame\",\"path\":\"" + J(outPath) + "\",\"w\":" + bmp.Width + ",\"h\":" + bmp.Height + ",\"fmt\":" + dup.Fmt + ",\"source\":\"" + source + "\",\"nonblack\":" + nb.ToString("F6") + "}");
                    bmp.Dispose();
                }
                else if (mode == "watch")
                {
                    double secs = args.Length > 1 ? double.Parse(args[1]) : 10;
                    string dir = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "gpuview");
                    int minArea = args.Length > 3 ? int.Parse(args[3]) : 400; // ignore tiny blinkers (cursor)
                    Directory.CreateDirectory(dir);
                    var t0 = DateTime.UtcNow; int n = 0;
                    // prime: get baseline frame
                    List<RECT> dirty; bool to;
                    Bitmap prev = null;
                    for (int i = 0; i < 10 && prev == null; i++) prev = dup.Acquire(500, out dirty, out to);
                    if (prev != null)
                    {
                        string bp = Path.Combine(dir, "baseline.png");
                        prev.Save(bp, ImageFormat.Png);
                        Console.WriteLine("{\"event\":\"baseline\",\"path\":\"" + J(bp) + "\",\"w\":" + dup.W + ",\"h\":" + dup.H + "}");
                    }
                    while ((DateTime.UtcNow - t0).TotalSeconds < secs)
                    {
                        var bmp = dup.Acquire(1000, out dirty, out to);
                        if (bmp == null) continue;        // timeout = nothing changed
                        var raw = Merge(dirty, 8, dup.W, dup.H);
                        var keep = Refine(prev, bmp, raw, minArea, 8);
                        if (keep.Count > 0)
                        {
                            n++;
                            var sb = new StringBuilder();
                            sb.Append("{\"event\":\"change\",\"n\":").Append(n).Append(",\"t\":")
                              .Append(((DateTime.UtcNow - t0).TotalMilliseconds).ToString("F0"))
                              .Append(",\"raw_regions\":").Append(raw.Count)
                              .Append(",\"raw_area\":").Append(SumArea(raw))
                              .Append(",\"refined_area\":").Append(SumArea(keep))
                              .Append(",\"regions\":[");
                            for (int i = 0; i < keep.Count; i++)
                            {
                                var r = keep[i];
                                string cp = Path.Combine(dir, "chg_" + n + "_" + i + ".png");
                                SaveCrop(bmp, r, cp);
                                if (i > 0) sb.Append(",");
                                sb.Append("{\"x\":").Append(r.l).Append(",\"y\":").Append(r.t)
                                  .Append(",\"w\":").Append(r.r - r.l).Append(",\"h\":").Append(r.b - r.t)
                                  .Append(",\"crop\":\"").Append(J(cp)).Append("\"}");
                            }
                            sb.Append("]}");
                            Console.WriteLine(sb.ToString());
                        }
                        if (prev != null) prev.Dispose();
                        prev = bmp;
                    }
                    if (prev != null) prev.Dispose();
                    Console.WriteLine("{\"event\":\"done\",\"changes\":" + n + "}");
                }
                else
                {
                    Console.WriteLine("{\"event\":\"error\",\"msg\":\"unknown mode " + J(mode) + "\"}");
                    return 1;
                }
            }
            return 0;
        }
        catch (COMException e)
        {
            Console.WriteLine("{\"event\":\"error\",\"hr\":\"0x" + e.HResult.ToString("X8") + "\",\"msg\":\"" + J(e.Message) + "\"}");
            return 3;
        }
    }
}
