using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace IconGen;

/// <summary>
/// Schreibt ein Windows-.ico mit allen Groessen. Kleine Groessen (<= 48 px) als klassischer
/// 32-bpp-DIB mit AND-Maske (maxte Kompatibilitaet, auch in alten Shell-Ansichten),
/// grosse Groessen als PNG-Eintrag (klein, verlustfrei, von Windows Vista an unterstuetzt).
/// </summary>
internal static class IcoFile
{
    public static void Write(string path, IReadOnlyDictionary<int, Bitmap> images)
    {
        List<int> sizes = new List<int>(images.Keys);
        sizes.Sort();

        List<byte[]> payloads = new List<byte[]>(sizes.Count);

        foreach (int size in sizes)
            payloads.Add(size >= 64 ? ToPng(images[size]) : ToDib(images[size]));

        using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(stream))
        {
            // ICONDIR
            w.Write((ushort)0);              // idReserved
            w.Write((ushort)1);              // idType = Icon
            w.Write((ushort)sizes.Count);    // idCount

            int offset = 6 + 16 * sizes.Count;
            for (int i = 0; i < sizes.Count; i++)
            {
                int size = sizes[i];
                byte[] payload = payloads[i];

                w.Write((byte)(size >= 256 ? 0 : size));   // bWidth  (0 = 256)
                w.Write((byte)(size >= 256 ? 0 : size));   // bHeight (0 = 256)
                w.Write((byte)0);                           // bColorCount
                w.Write((byte)0);                           // bReserved
                w.Write((ushort)1);                         // wPlanes
                w.Write((ushort)32);                        // wBitCount
                w.Write((uint)payload.Length);              // dwBytesInRes
                w.Write((uint)offset);                      // dwImageOffset

                offset += payload.Length;
            }

            foreach (byte[] payload in payloads)
                w.Write(payload);

            w.Flush();
        }
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    private static byte[] ToDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        byte[] pixels = PixelReader.Read(bitmap);

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            // BITMAPINFOHEADER: hoehere = XOR (ARGB) + AND-Maske, daher 2 * height.
            w.Write((uint)40);
            w.Write((int)width);
            w.Write((int)(height * 2));
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)0);            // BI_RGB
            w.Write((uint)(width * height * 4 + RowStride(width) * height));
            w.Write((int)0);
            w.Write((int)0);
            w.Write((uint)0);
            w.Write((uint)0);

            // XOR-Daten: Pixel von unten nach oben, BGRA (Speicherlayout von Format32bppArgb).
            byte[] row = new byte[width * 4];
            for (int y = height - 1; y >= 0; y--)
            {
                Buffer.BlockCopy(pixels, y * width * 4, row, 0, row.Length);
                w.Write(row);
            }

            // AND-Maske: 1 = transparent. Zeilen auf 32 Bit aufgefuellt, ebenfalls bottom-up.
            int stride = RowStride(width);
            byte[] mask = new byte[stride];

            for (int y = height - 1; y >= 0; y--)
            {
                Array.Clear(mask, 0, mask.Length);

                for (int x = 0; x < width; x++)
                {
                    if (pixels[(y * width + x) * 4 + 3] == 0)
                        mask[x >> 3] |= (byte)(0x80 >> (x & 7));
                }

                w.Write(mask);
            }

            w.Flush();
            return ms.ToArray();
        }
    }

    private static int RowStride(int width) => ((width + 31) / 32) * 4;
}
