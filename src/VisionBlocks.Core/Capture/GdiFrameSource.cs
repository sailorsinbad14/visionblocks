using System.Diagnostics;
using OpenCvSharp;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using VisionBlocks.Core.Win32;

namespace VisionBlocks.Core.Capture;

/// <summary>
/// BitBlt 兜底（WGC 不可用时用）。没有回调，调用 TryGetLatest 时现截一张客户区。
/// 遇到翻转模型或独占全屏会截到黑屏，所以只当备选。
/// </summary>
public sealed unsafe class GdiFrameSource : IFrameSource
{
    public string Name => "BitBlt";
    public event FrameHook? FrameArrived;
    public long LatestSeq => _seq;
    private IntPtr _hwnd;
    private long _seq;

    public void Start(IntPtr hwnd) => _hwnd = hwnd;

    public Frame? TryGetLatest()
    {
        if (!WindowInfo.IsAlive(_hwnd) || WindowInfo.IsMinimized(_hwnd)) return null;
        if (!WindowInfo.TryGetClientOnScreen(_hwnd, out var c) || c.W <= 0 || c.H <= 0) return null;
        long arr = Stopwatch.GetTimestamp();
        var hwnd = (HWND)_hwnd;
        HDC src = PInvoke.GetDC(hwnd);
        HDC mem = PInvoke.CreateCompatibleDC(src);
        HBITMAP bmp = PInvoke.CreateCompatibleBitmap(src, c.W, c.H);
        HGDIOBJ old = PInvoke.SelectObject(mem, bmp);
        try
        {
            if (!PInvoke.BitBlt(mem, 0, 0, c.W, c.H, src, 0, 0, ROP_CODE.SRCCOPY)) return null;
            var mat = new Mat(c.H, c.W, MatType.CV_8UC4);
            var bi = new BITMAPINFO();
            bi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bi.bmiHeader.biWidth = c.W;
            bi.bmiHeader.biHeight = -c.H;   // 自上而下
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0; // BI_RGB
            PInvoke.SelectObject(mem, old);
            int lines = PInvoke.GetDIBits(mem, bmp, 0, (uint)c.H, (void*)mat.Data, &bi, DIB_USAGE.DIB_RGB_COLORS);
            if (lines != c.H) { mat.Dispose(); return null; }
            _seq++;
            FrameArrived?.Invoke(mat, _seq, arr, 0);
            return new Frame { Bgra = mat, Seq = _seq, ArrivalQpc = arr, SystemQpc = 0 };
        }
        finally
        {
            PInvoke.DeleteObject(bmp);
            PInvoke.DeleteDC(mem);
            PInvoke.ReleaseDC(hwnd, src);
        }
    }

    public void Dispose() { }
}
