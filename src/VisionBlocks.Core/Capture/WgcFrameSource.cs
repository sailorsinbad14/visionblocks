using System.Diagnostics;
using OpenCvSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Core.Capture;

/// <summary>
/// Windows Graphics Capture，采用「到达即取」：帧池只留 2 帧，回调里把最新一帧拷到 CPU，旧的直接覆盖。
/// D3D11 互操作用 Vortice；要留意线程亲和、内存抖动和捕获时的黄色边框。
/// </summary>
public sealed class WgcFrameSource : IFrameSource
{
    public string Name => "WGC";
    public event FrameHook? FrameArrived;
    public long LatestSeq => Interlocked.Read(ref _seq);
    public string? BorderNote { get; private set; }

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private ID3D11Texture2D? _staging;
    private IntPtr _hwnd;

    private readonly object _lock = new();
    private Mat? _latest;          // 复用的最新帧缓冲区，避免每帧 new 一块 8MB
    private long _seq, _arr, _sys;

    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device, out _ctx).CheckError();
        using (var dxgi = _device!.QueryInterface<IDXGIDevice>())
            _winrtDevice = WinRtInterop.CreateWinRtDevice(dxgi.NativePointer);

        _item = WinRtInterop.CreateItemForWindow(hwnd);
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            _session.IsCursorCaptureEnabled = false;
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                _session.IsBorderRequired = false;
                BorderNote = "已关闭黄色边框";
            }
            else BorderNote = "系统不支持关闭黄色边框";
        }
        catch (Exception e) { BorderNote = "关闭黄色边框失败：" + e.Message; }
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        long arr = Stopwatch.GetTimestamp();
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;
        var content = frame.ContentSize;
        try
        {
            if (WindowInfo.IsMinimized(_hwnd)) return;                       // 最小化时不发布新帧
            if (!WindowInfo.TryGetClientInVisibleBounds(_hwnd, out var crop)) return;
            int x0 = Math.Max(0, crop.X), y0 = Math.Max(0, crop.Y);
            int w = Math.Min(crop.W, content.Width - x0), h = Math.Min(crop.H, content.Height - y0);
            if (w <= 0 || h <= 0) return;

            IntPtr texPtr = WinRtInterop.GetDxgiInterface(frame.Surface, typeof(ID3D11Texture2D).GUID);
            using var tex = new ID3D11Texture2D(texPtr);
            var desc = tex.Description;
            EnsureStaging(desc.Width, desc.Height);
            long sys = (long)(frame.SystemRelativeTime.Ticks * (Stopwatch.Frequency / 1e7));

            lock (_lock)   // D3D11 立即上下文不是线程安全的，拷贝、Map、发布都在同一把锁里
            {
                _ctx!.CopyResource(_staging!, tex);
                var box = _ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    if (_latest == null || _latest.Width != w || _latest.Height != h)
                    {
                        _latest?.Dispose();
                        _latest = new Mat(h, w, MatType.CV_8UC4);
                    }
                    unsafe
                    {
                        byte* src = (byte*)box.DataPointer + y0 * box.RowPitch + x0 * 4;
                        byte* dst = (byte*)_latest.Data;
                        long dstStep = _latest.Step();
                        for (int y = 0; y < h; y++)
                            Buffer.MemoryCopy(src + (long)y * box.RowPitch, dst + y * dstStep, dstStep, (long)w * 4);
                    }
                    _seq++;
                    _arr = arr;
                    _sys = sys;
                    FrameArrived?.Invoke(_latest, _seq, arr, sys);
                }
                finally { _ctx.Unmap(_staging!, 0); }
            }
        }
        finally
        {
            // 窗口尺寸变了就重建帧池（线程亲和：在同一个回调线程里重建）
            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
            {
                _poolSize = content;
                _staging?.Dispose();
                _staging = null;
                sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
        }
    }

    private void EnsureStaging(uint w, uint h)
    {
        if (_staging != null)
        {
            var d = _staging.Description;
            if (d.Width == w && d.Height == h) return;
            _staging.Dispose();
        }
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None
        });
    }

    public Frame? TryGetLatest()
    {
        lock (_lock)
        {
            if (_latest == null || WindowInfo.IsMinimized(_hwnd) || !WindowInfo.IsAlive(_hwnd)) return null;
            return new Frame { Bgra = _latest.Clone(), Seq = _seq, ArrivalQpc = _arr, SystemQpc = _sys };
        }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        if (_pool != null) { _pool.FrameArrived -= OnFrameArrived; try { _pool.Dispose(); } catch { } }
        lock (_lock) { _latest?.Dispose(); _latest = null; }
        _staging?.Dispose();
        _ctx?.Dispose();
        _device?.Dispose();
    }
}
