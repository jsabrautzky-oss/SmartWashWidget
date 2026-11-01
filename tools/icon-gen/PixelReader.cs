using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IconGen;

/// <summary>
/// Pixel direkt aus dem Speicher der Bitmap (BGRA, Zeilensprung beachten): noetig fuer
/// exaktes Herunterrechnen und fuer das Schreiben der ICO-Daten.
/// </summary>
internal static class PixelReader
{
    public static byte[] Read(Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        byte[] buffer = new byte[width * height * 4];

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            if (data.Stride == width * 4)
            {
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                byte[] rows = new byte[data.Stride * height];
                Marshal.Copy(data.Scan0, rows, 0, rows.Length);

                for (int y = 0; y < height; y++)
                    Array.Copy(rows, y * data.Stride, buffer, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return buffer;
    }

    public static void Write(Bitmap bitmap, byte[] buffer)
    {
        int width = bitmap.Width, height = bitmap.Height;

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            if (data.Stride == width * 4)
            {
                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            else
            {
                byte[] rows = new byte[data.Stride * height];

                for (int y = 0; y < height; y++)
                    Array.Copy(buffer, y * width * 4, rows, y * data.Stride, width * 4);

                Marshal.Copy(rows, 0, data.Scan0, rows.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
