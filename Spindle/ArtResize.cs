using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Spindle;

/// <summary>
/// Downscales oversized embedded album art for the iPod copy (some iPods/Rockbox choke on huge covers).
/// Only ever touches the device copy — the library source is never modified here.
/// </summary>
public static class ArtResize
{
    /// <summary>Resize image bytes so the long edge ≤ maxPx (JPEG). Returns null if already small or on failure.</summary>
    public static byte[]? Fit(byte[]? data, int maxPx)
    {
        if (data == null || data.Length == 0 || maxPx <= 0) return null;
        try
        {
            using var bmp = SKBitmap.Decode(data);
            if (bmp == null) return null;
            int w = bmp.Width, h = bmp.Height, m = Math.Max(w, h);
            if (m <= maxPx) return null;
            float scale = (float)maxPx / m;
            int nw = Math.Max(1, (int)(w * scale)), nh = Math.Max(1, (int)(h * scale));
            using var resized = bmp.Resize(new SKImageInfo(nw, nh), SKFilterQuality.Medium);
            if (resized == null) return null;
            using var img = SKImage.FromBitmap(resized);
            using var enc = img.Encode(SKEncodedImageFormat.Jpeg, 88);
            return enc?.ToArray();
        }
        catch { return null; }
    }

    /// <summary>Shrink any oversized embedded pictures in a file in place (used on the iPod copy after a raw copy).</summary>
    public static void ShrinkInFile(string path, int maxPx)
    {
        if (maxPx <= 0) return;
        try
        {
            var t = new ATL.Track(path);
            if (t.EmbeddedPictures.Count == 0) return;
            var newPics = new List<ATL.PictureInfo>();
            bool changed = false;
            foreach (var p in t.EmbeddedPictures)
            {
                var rz = Fit(p.PictureData, maxPx);
                if (rz != null) { newPics.Add(ATL.PictureInfo.fromBinaryData(rz)); changed = true; }
                else newPics.Add(p);
            }
            if (!changed) return;
            t.EmbeddedPictures.Clear();
            foreach (var p in newPics) t.EmbeddedPictures.Add(p);
            t.Save();
        }
        catch { }
    }
}
