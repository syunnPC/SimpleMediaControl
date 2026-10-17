using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace PlayerApp;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, BitmapImage?> _iconCache = new(StringComparer.Ordinal);

    private async Task UpdateAlbumArtAsync(IRandomAccessStreamReference? thumbnail, int generation)
    {
        var analysis = await AnalyzeArtAsync(thumbnail, generation);
        if (generation != _mediaGeneration)
        {
            return;
        }
        if (analysis is not null)
        {
            AlbumArtImage.Source = analysis.Image;
            AlbumArtImage.Visibility = Visibility.Visible;
            ArtPlaceholder.Visibility = Visibility.Collapsed;
            ApplyPalette(analysis.Palette);
            return;
        }

        AlbumArtImage.Source = null;
        AlbumArtImage.Visibility = Visibility.Collapsed;
        ArtPlaceholder.Visibility = Visibility.Visible;
        ClearArtPalette();
    }

    private sealed record ArtAnalysis(BitmapImage Image, AlbumPalette Palette);

    private async Task<ArtAnalysis?> AnalyzeArtAsync(IRandomAccessStreamReference? thumbnail, int generation)
    {
        try
        {
            if (thumbnail is null)
            {
                return null;
            }
            using var stream = await thumbnail.OpenReadAsync();
            if (generation != _mediaGeneration)
            {
                return null;
            }
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var frame = await decoder.GetFrameAsync(0);
            var maxSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
            var scale = maxSide > 384 ? 384.0 / maxSide : 1.0;
            var scaledWidth = Math.Max(1, (uint)Math.Round(frame.PixelWidth * scale));
            var scaledHeight = Math.Max(1, (uint)Math.Round(frame.PixelHeight * scale));
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
            };
            var provider = await frame.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
            var data = provider.DetachPixelData();
            if (generation != _mediaGeneration)
            {
                return null;
            }
            var width = (int)scaledWidth;
            var height = (int)scaledHeight;
            if (width <= 0 || height <= 0 || data.Length < width * height * 4)
            {
                return null;
            }

            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (data[(y * width + x) * 4 + 3] > 16)
                    {
                        if (x < minX)
                        {
                            minX = x;
                        }
                        if (x > maxX)
                        {
                            maxX = x;
                        }
                        if (y < minY)
                        {
                            minY = y;
                        }
                        if (y > maxY)
                        {
                            maxY = y;
                        }
                    }
                }
            }
            if (maxX < 0)
            {
                return null;
            }
            var cropW = maxX - minX + 1;
            var cropH = maxY - minY + 1;
            var palette = await Task.Run(() => AlbumPalette.Extract(data, width, height));
            if (generation != _mediaGeneration)
            {
                return null;
            }

            var cropBytes = new byte[cropW * cropH * 4];
            for (var y = 0; y < cropH; y++)
            {
                var sourceIndex = ((minY + y) * width + minX) * 4;
                Array.Copy(data, sourceIndex, cropBytes, y * cropW * 4, cropW * 4);
            }

            var pngBytes = ArtBytesToPng(cropBytes, cropW, cropH);
            if (pngBytes is null)
            {
                return null;
            }
            var image = WriteTempPng(pngBytes, $"art{DateTime.Now.Ticks:X}_{generation:X}");
            if (image is null)
            {
                return null;
            }
            if (palette is not { } resultPalette)
            {
                return null;
            }
            return new ArtAnalysis(image, resultPalette);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ArtBytesToPng(byte[] bgraBytes, int width, int height)
    {
        try
        {
            using var drawingBitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rectangle = new System.Drawing.Rectangle(0, 0, width, height);
            var bits = drawingBitmap.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bgraBytes, 0, bits.Scan0, bgraBytes.Length);
            drawingBitmap.UnlockBits(bits);
            using var memory = new System.IO.MemoryStream();
            drawingBitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? WriteTempPng(byte[] pngBytes, string key)
    {
        try
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlayerAppIcons");
            System.IO.Directory.CreateDirectory(folder);
            var fileName = System.IO.Path.Combine(folder, $"{key}.png");
            System.IO.File.WriteAllBytes(fileName, pngBytes);
            CleanOldTempPngs();
            return new BitmapImage(new Uri(fileName));
        }
        catch
        {
            return null;
        }
    }

    private static void CleanOldTempPngs()
    {
        try
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlayerAppIcons");
            if (!System.IO.Directory.Exists(folder))
            {
                return;
            }
            var cutoff = System.DateTime.Now.Subtract(TimeSpan.FromMinutes(20));
            foreach (var file in System.IO.Directory.GetFiles(folder, "*.png"))
            {
                try
                {
                    if (System.IO.File.GetLastWriteTime(file) < cutoff)
                    {
                        System.IO.File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private async Task FillSessionButtonIconAsync(Button button, GlobalSystemMediaTransportControlsSession session)
    {
        var aumid = session.SourceAppUserModelId;
        if (string.IsNullOrEmpty(aumid))
        {
            return;
        }
        if (_iconCache.TryGetValue(aumid, out var cached))
        {
            if (cached is not null)
            {
                SwapSessionButtonContent(button, cached);
            }
            return;
        }
        var bitmap = await GetAppIconAsync(aumid);
        _iconCache[aumid] = bitmap;
        if (bitmap is not null
            && ReferenceEquals(button.Tag, session)
            && SessionSwitchPanel.Children.Contains(button))
        {
            SwapSessionButtonContent(button, bitmap);
        }
    }

    private static void SwapSessionButtonContent(Button button, BitmapImage bitmap)
    {
        button.Content = new Image
        {
            Source = bitmap,
            Width = 16,
            Height = 16
        };
    }

    private async Task<BitmapImage?> GetAppIconAsync(string aumid)
    {
        try
        {
            var appInfo = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid);
            var logo = appInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var logoStream = await logo.OpenReadAsync();
            var trimmedPng = await ExtractTightPngFromLogoStreamAsync(logoStream);
            if (trimmedPng is null)
            {
                return null;
            }
            return LoadIconImageFromPng(aumid, trimmedPng);
        }
        catch
        {
        }
        try
        {
            var shellPng = ExtractAppFolderIconPng(aumid);
            if (shellPng is not null)
            {
                return LoadIconImageFromPng(aumid, shellPng);
            }
        }
        catch
        {
        }
        try
        {
            var exePath = FindMediaProcessPath(aumid);
            if (!string.IsNullOrEmpty(exePath))
            {
                var iconBytes = ExtractPngIconBytes(exePath);
                if (iconBytes is not null)
                {
                    return LoadIconImageFromPng(aumid, iconBytes);
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static byte[]? ExtractAppFolderIconPng(string aumid)
    {
        nint hbitmap = 0;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid, 0, iid, out object? boxed);
            if (boxed is not IShellItemImageFactory factory)
            {
                return null;
            }
            var result = factory.GetImage(new NativeSize { Width = 64, Height = 64 }, 0u, out hbitmap);
            if (result != 0 || hbitmap == 0)
            {
                return null;
            }
            return HBitmapToPngBytes(hbitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != 0)
            {
                _ = DeleteObject(hbitmap);
            }
        }
    }

    private static byte[]? HBitmapToPngBytes(nint hbitmap)
    {
        try
        {
            var bitmapInfo = new Bitmap();
            if (GetObjectW(hbitmap, System.Runtime.InteropServices.Marshal.SizeOf<Bitmap>(), ref bitmapInfo) == 0)
            {
                return null;
            }
            var width = bitmapInfo.BmWidth;
            var height = Math.Abs(bitmapInfo.BmHeight);
            if (width <= 0 || height <= 0)
            {
                return null;
            }
            var v5 = new BitmapV5Header
            {
                Bv5Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapV5Header>(),
                Bv5Width = width,
                Bv5Height = -height,
                Bv5Planes = 1,
                Bv5BitCount = 32,
                Bv5Compression = 3,
                Bv5RedMask = 0x00FF0000,
                Bv5GreenMask = 0x0000FF00,
                Bv5BlueMask = 0x000000FF,
                Bv5AlphaMask = 0xFF000000,
                Bv5CSType = 0x57696E20,
                Bv5Intent = 4
            };
            var buffer = new byte[width * 4 * height];
            var hdc = GetDC(0);
            try
            {
                var headerSize = System.Runtime.InteropServices.Marshal.SizeOf<BitmapV5Header>();
                var headerPointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(headerSize);
                try
                {
                    System.Runtime.InteropServices.Marshal.StructureToPtr(v5, headerPointer, false);
                    var gpuHandle = GetDIBits(hdc, hbitmap, 0, (uint)height, buffer, headerPointer, 0);
                    if (gpuHandle != height)
                    {
                        return null;
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(headerPointer);
                }
            }
            finally
            {
                _ = ReleaseDC(0, hdc);
            }
            return ArtBytesToPng(buffer, width, height);
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out nint phbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int BmType;
        public int BmWidth;
        public int BmHeight;
        public int BmWidthBytes;
        public short BmPlanes;
        public short BmBitsPixel;
        public nint BmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapV5Header
    {
        public uint Bv5Size;
        public int Bv5Width;
        public int Bv5Height;
        public ushort Bv5Planes;
        public ushort Bv5BitCount;
        public uint Bv5Compression;
        public uint Bv5SizeImage;
        public int Bv5XPelsPerMeter;
        public int Bv5YPelsPerMeter;
        public uint Bv5ClrUsed;
        public uint Bv5ClrImportant;
        public uint Bv5RedMask;
        public uint Bv5GreenMask;
        public uint Bv5BlueMask;
        public uint Bv5AlphaMask;
        public uint Bv5CSType;
        public long Bv5EndpointsR;
        public long Bv5EndpointsG;
        public long Bv5EndpointsB;
        public uint Bv5GammaR;
        public uint Bv5GammaG;
        public uint Bv5GammaB;
        public uint Bv5Intent;
        public uint Bv5ProfileData;
        public uint Bv5ProfileSize;
        public uint Bv5Reserved;
    }

    [DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object? shellItem);

    [DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetObjectW")]
    private static extern int GetObjectW(nint hObject, int size, ref Bitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbitmap, uint startScan, uint scanLines, byte[] bits, nint info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    private static BitmapImage? LoadIconImageFromPng(string aumid, byte[] pngBytes)
    {
        try
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlayerAppIcons");
            System.IO.Directory.CreateDirectory(folder);
            var fileName = System.IO.Path.Combine(folder, $"{aumid.GetHashCode():X}.png");
            System.IO.File.WriteAllBytes(fileName, pngBytes);
            return new BitmapImage(new Uri(fileName));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> ExtractTightPngFromLogoStreamAsync(Windows.Storage.Streams.IRandomAccessStream logoStream)
    {
        try
        {
            logoStream.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(logoStream);
            var frame = await decoder.GetFrameAsync(0);
            var provider = await frame.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
            var data = provider.DetachPixelData();
            var width = (int)frame.PixelWidth;
            var height = (int)frame.PixelHeight;
            if (width <= 0 || height <= 0 || data.Length < width * height * 4)
            {
                return null;
            }

            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (data[(y * width + x) * 4 + 3] > 16)
                    {
                        if (x < minX)
                        {
                            minX = x;
                        }
                        if (x > maxX)
                        {
                            maxX = x;
                        }
                        if (y < minY)
                        {
                            minY = y;
                        }
                        if (y > maxY)
                        {
                            maxY = y;
                        }
                    }
                }
            }
            if (maxX < 0)
            {
                return null;
            }
            var cropW = maxX - minX + 1;
            var cropH = maxY - minY + 1;
            var cropBytes = new byte[cropW * cropH * 4];
            for (var y = 0; y < cropH; y++)
            {
                var sourceIndex = ((minY + y) * width + minX) * 4;
                System.Array.Copy(data, sourceIndex, cropBytes, y * cropW * 4, cropW * 4);
            }

            return ArtBytesToPng(cropBytes, cropW, cropH);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ExtractPngIconBytes(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null)
            {
                return null;
            }
            using var drawingBitmap = icon.ToBitmap();
            using var memory = new System.IO.MemoryStream();
            drawingBitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private string? FindMediaProcessPath(string aumid)
    {
        var manager = _audioSessionManager;
        if (manager is null)
        {
            return null;
        }
        var candidates = BuildProcessNameCandidates(aumid);
        var packageTokens = BuildPackagePathTokens(aumid);
        SessionCollection sessions;
        try
        {
            sessions = manager.Sessions;
        }
        catch
        {
            return null;
        }
        for (var i = 0; i < sessions.Count; i++)
        {
            AudioSessionControl audioSession;
            try
            {
                audioSession = sessions[i];
                if (audioSession.IsSystemSoundsSession)
                {
                    continue;
                }
                var procName = "";
                string? procPath = null;
                var pid = (int)audioSession.GetProcessID;
                if (pid > 0)
                {
                    try
                    {
                        var process = Process.GetProcessById(pid);
                        procName = process.ProcessName?.ToLowerInvariant() ?? "";
                        procPath = process.MainModule?.FileName;
                    }
                    catch
                    {
                    }
                }
                var pathMatch = false;
                var identifier = audioSession.GetSessionIdentifier ?? "";
                var bar = identifier.IndexOf('|');
                if (bar >= 0)
                {
                    var next = identifier.IndexOf('%', bar + 1);
                    var path = (next > bar ? identifier[(bar + 1)..next] : identifier[(bar + 1)..]).ToLowerInvariant();
                    if (packageTokens is not null && IsPathInPackage(path, packageTokens))
                    {
                        pathMatch = true;
                    }
                    if (!pathMatch)
                    {
                        var slash = path.LastIndexOf('\\');
                        var fileName = slash >= 0 ? path[(slash + 1)..] : path;
                        var dot = fileName.LastIndexOf('.');
                        var stem = dot > 0 ? fileName[..dot] : fileName;
                        if (stem.Length > 0 && candidates.Contains(stem))
                        {
                            pathMatch = true;
                        }
                    }
                }
                var procMatch = procName.Length > 0 && candidates.Contains(procName);
                if ((procMatch || pathMatch) && !string.IsNullOrEmpty(procPath))
                {
                    return procPath;
                }
            }
            catch
            {
                continue;
            }
        }
        return null;
    }
}
