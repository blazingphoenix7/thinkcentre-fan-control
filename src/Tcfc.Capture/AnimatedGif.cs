using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace Tcfc.Capture;

/// <summary>
/// Writes an infinitely looping animated GIF from a list of frames.
/// <see cref="GifBitmapEncoder"/> concatenates the images but emits no timing
/// or loop metadata, so the byte stream is rewritten afterwards: a
/// NETSCAPE2.0 application extension (loop count 0 = forever) is inserted
/// right after the logical screen descriptor / global color table, and every
/// image descriptor gets a Graphic Control Extension carrying the frame
/// delay. The rewrite is a real block walk of the GIF grammar, not a byte
/// pattern search, so local color tables and existing extensions survive.
/// </summary>
public static class AnimatedGif
{
    public static void Save(string path, IReadOnlyList<Bitmap> frames, int delayMs)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            throw new ArgumentException("At least one frame is required.", nameof(frames));

        var encoder = new GifBitmapEncoder();
        foreach (Bitmap frame in frames)
            encoder.Frames.Add(ToBitmapFrame(frame));

        byte[] plain;
        using (var buffer = new MemoryStream())
        {
            encoder.Save(buffer);
            plain = buffer.ToArray();
        }

        // GIF time unit is 1/100 s; browsers treat delays below 2 cs as 10 cs,
        // so clamp to keep the declared timing honest.
        int delayCs = Math.Max(2, (int)Math.Round(delayMs / 10.0));
        File.WriteAllBytes(path, WithLoopAndDelays(plain, delayCs));
    }

    /// <summary>
    /// Round-trips a GDI+ bitmap into a WPF frame via an in-memory PNG —
    /// lossless, and avoids interop HBITMAP ownership pitfalls.
    /// </summary>
    private static BitmapFrame ToBitmapFrame(Bitmap bitmap)
    {
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        buffer.Position = 0;
        var frame = BitmapFrame.Create(buffer, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// Walks the encoder's GIF stream block by block and rebuilds it with a
    /// NETSCAPE2.0 loop extension plus a per-frame Graphic Control Extension
    /// (delay in centiseconds, disposal "leave in place"). Existing GCEs are
    /// patched rather than duplicated.
    /// </summary>
    private static byte[] WithLoopAndDelays(byte[] src, int delayCs)
    {
        if (src.Length < 13 || src[0] != (byte)'G' || src[1] != (byte)'I' || src[2] != (byte)'F')
            throw new InvalidDataException("The encoder did not produce a GIF stream.");

        // Header (6) + logical screen descriptor (7) + optional global color table.
        int headerLen = 13;
        byte lsdPacked = src[10];
        if ((lsdPacked & 0x80) != 0)
            headerLen += 3 * (2 << (lsdPacked & 0x07));
        if (src.Length < headerLen)
            throw new InvalidDataException("Truncated GIF header.");

        var output = new MemoryStream(src.Length + 64);
        output.Write(src, 0, headerLen);

        // Animation blocks are GIF89a features; stamp the version accordingly.
        output.Position = 3;
        output.Write(new[] { (byte)'8', (byte)'9', (byte)'a' }, 0, 3);
        output.Position = output.Length;

        // NETSCAPE2.0 application extension: sub-block 0x01 + loop count 0 (forever).
        output.Write(new byte[]
        {
            0x21, 0xFF, 0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
            (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01, 0x00, 0x00,
            0x00,
        }, 0, 19);

        int pos = headerLen;
        bool pendingGce = false;
        while (pos < src.Length)
        {
            byte marker = src[pos];

            if (marker == 0x3B) // trailer
            {
                output.WriteByte(0x3B);
                break;
            }

            if (marker == 0x21) // extension block
            {
                if (pos + 2 > src.Length)
                    throw new InvalidDataException("Truncated GIF extension block.");
                byte label = src[pos + 1];
                int end = SkipSubBlocks(src, pos + 2);

                if (label == 0xF9 && src[pos + 2] == 0x04)
                {
                    // Existing Graphic Control Extension: patch delay + disposal.
                    var gce = new byte[end - pos];
                    Array.Copy(src, pos, gce, 0, gce.Length);
                    gce[3] = (byte)((gce[3] & 0xE3) | 0x04); // disposal 1: leave frame in place
                    gce[4] = (byte)(delayCs & 0xFF);
                    gce[5] = (byte)((delayCs >> 8) & 0xFF);
                    output.Write(gce, 0, gce.Length);
                    pendingGce = true;
                }
                else
                {
                    output.Write(src, pos, end - pos);
                }
                pos = end;
            }
            else if (marker == 0x2C) // image descriptor
            {
                if (!pendingGce)
                {
                    output.Write(new byte[]
                    {
                        0x21, 0xF9, 0x04,
                        0x04, // disposal 1: leave frame in place, no transparency
                        (byte)(delayCs & 0xFF), (byte)((delayCs >> 8) & 0xFF),
                        0x00, 0x00,
                    }, 0, 8);
                }
                pendingGce = false;

                if (pos + 10 > src.Length)
                    throw new InvalidDataException("Truncated GIF image descriptor.");
                int end = pos + 10;
                byte idPacked = src[pos + 9];
                if ((idPacked & 0x80) != 0)
                    end += 3 * (2 << (idPacked & 0x07)); // local color table
                if (end >= src.Length)
                    throw new InvalidDataException("Truncated GIF image data.");
                end++; // LZW minimum code size byte
                end = SkipSubBlocks(src, end);

                output.Write(src, pos, end - pos);
                pos = end;
            }
            else
            {
                throw new InvalidDataException($"Unexpected GIF block marker 0x{marker:X2} at offset {pos}.");
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Advances over a chain of data sub-blocks (length byte + payload,
    /// terminated by a zero length byte) and returns the offset just past the
    /// terminator.
    /// </summary>
    private static int SkipSubBlocks(byte[] src, int pos)
    {
        while (true)
        {
            if (pos >= src.Length)
                throw new InvalidDataException("Truncated GIF data sub-blocks.");
            byte len = src[pos];
            pos++;
            if (len == 0)
                return pos;
            pos += len;
        }
    }
}
