using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace IconGen;

/// <summary>
/// Erzeugt das Programm-Symbol fuer EXE, Installer und Verknuepfungen:
///   Assets\SmartWashWidget.ico  (16, 24, 32, 48, 64, 128, 256 px)
///   Assets\preview\*.png        (Einzelgroessen + Kontrollblatt)
///
/// Aufruf aus dem Projektstamm:   dotnet run --project tools\icon-gen
/// </summary>
internal static class Program
{
    private static readonly int[] IconSizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();

        string assets = Path.Combine(root, "Assets");
        string preview = Path.Combine(assets, "preview");
        Directory.CreateDirectory(preview);

        Console.WriteLine("Zeichne Bullauge: Trommel + Wasserwelle + Partikel ...");

        Dictionary<int, Bitmap> rendered = new Dictionary<int, Bitmap>();

        foreach (int size in IconSizes)
        {
            rendered[size] = IconArt.RenderIcon(size);
            Console.WriteLine($"  {size,3} px  (ueberabgetastet {size * 8} px)");
        }

        string icoPath = Path.Combine(assets, "SmartWashWidget.ico");
        IcoFile.Write(icoPath, rendered);
        Console.WriteLine("Icon geschrieben: " + icoPath + "  [" + new FileInfo(icoPath).Length + " Bytes]");

        foreach (KeyValuePair<int, Bitmap> entry in rendered)
            entry.Value.Save(Path.Combine(preview, "icon-" + entry.Key + ".png"), ImageFormat.Png);

        string sheetPath = Path.Combine(preview, "blatt.png");
        SheetWriter.Write(sheetPath, rendered);
        Console.WriteLine("Kontrollblatt:    " + sheetPath);

        foreach (Bitmap bitmap in rendered.Values)
            bitmap.Dispose();

        return 0;
    }
}
