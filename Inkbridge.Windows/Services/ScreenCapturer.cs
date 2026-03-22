using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace Inkbridge.Windows.Services;

public class ScreenCapturer : BackgroundService
{
    private readonly ILogger<ScreenCapturer> _logger;
    private readonly NetworkService _network;

    public ScreenCapturer(ILogger<ScreenCapturer> logger, NetworkService network)
    {
        _logger = logger;
        _network = network;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // run nicely in bg

        SharpDX.Direct3D11.Device d3dDevice = null;
        OutputDuplication duplicatedOutput = null;
        Texture2D screenTexture = null;

        try
        {
            var factory = new Factory1();
            var adapter = factory.GetAdapter1(0);
            d3dDevice = new SharpDX.Direct3D11.Device(adapter);
            var output = adapter.GetOutput(0);
            var output1 = output.QueryInterface<Output1>();

            duplicatedOutput = output1.DuplicateOutput(d3dDevice);
            var bounds = output.Description.DesktopBounds;
            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;

            var textureDesc = new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = width,
                Height = height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Staging
            };
            screenTexture = new Texture2D(d3dDevice, textureDesc);

            // Encoder parameters config for 70% quality
            var jci = GetEncoder(ImageFormat.Jpeg);
            var eps = new EncoderParameters(1);
            eps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    SharpDX.DXGI.Resource screenResource;
                    OutputDuplicateFrameInformation duplicateFrameInformation;

                    // Timeout of 20ms implies ~50fps max rate
                    var res = duplicatedOutput.TryAcquireNextFrame(20, out duplicateFrameInformation, out screenResource);
                    if (res.Failure)
                    {
                        continue;
                    }

                    using (var screenTexture2D = screenResource.QueryInterface<Texture2D>())
                    {
                        d3dDevice.ImmediateContext.CopyResource(screenTexture2D, screenTexture);
                    }

                    var mapSource = d3dDevice.ImmediateContext.MapSubresource(screenTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

                    using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    {
                        var boundsRect = new System.Drawing.Rectangle(0, 0, width, height);
                        var mapDest = bitmap.LockBits(boundsRect, ImageLockMode.WriteOnly, bitmap.PixelFormat);
                        var sourcePtr = mapSource.DataPointer;
                        var destPtr = mapDest.Scan0;

                        for (int y = 0; y < height; y++)
                        {
                            Utilities.CopyMemory(destPtr, sourcePtr, width * 4);
                            sourcePtr = IntPtr.Add(sourcePtr, mapSource.RowPitch);
                            destPtr = IntPtr.Add(destPtr, mapDest.Stride);
                        }

                        bitmap.UnlockBits(mapDest);

                        d3dDevice.ImmediateContext.UnmapSubresource(screenTexture, 0);

                        // Extract foreground window bounds to crop if needed (Fallback: Whole screen)
                        var hwnd = GetForegroundWindow();
                        System.Drawing.Rectangle cropRect = boundsRect;
                        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect))
                        {
                            cropRect = new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                            cropRect.Intersect(boundsRect);
                        }

                        if (cropRect.Width > 0 && cropRect.Height > 0)
                        {
                            using (var cropped = bitmap.Clone(cropRect, bitmap.PixelFormat))
                            {
                                using (var ms = new MemoryStream())
                                {
                                    cropped.Save(ms, jci, eps);
                                    await _network.BroadcastFrameAsync(ms.ToArray());
                                }
                            }
                        }
                    }

                    screenResource.Dispose();
                    duplicatedOutput.ReleaseFrame();
                }
                catch (SharpDXException e) when (e.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Capture error: {ex.Message}");
                    await Task.Delay(1000, stoppingToken); 
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"DXGI Init Error: {ex}");
        }
        finally
        {
            screenTexture?.Dispose();
            duplicatedOutput?.Dispose();
            d3dDevice?.Dispose();
        }
    }

    private ImageCodecInfo GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageDecoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == format.Guid)
            {
                return codec;
            }
        }
        return null;
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
