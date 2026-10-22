# Dibuja el icono de Cabina (un tablero de cabina de noche: dial con aguja y las tres luces de estado) y lo guarda
# como src\Cabina\icono.ico con varios tamaños (PNG dentro del ICO). En 16-24 px solo van las luces.
#   powershell -File tools\generar-icono.ps1 [-Muestras <carpeta>]   (Muestras: deja también cada tamaño en PNG)
param([string]$Muestras = '')
$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconoCabina
{
    static readonly Color Fondo = Color.FromArgb(0x1C, 0x1F, 0x25);
    static readonly Color FondoAlto = Color.FromArgb(0x2A, 0x2E, 0x37);
    static readonly Color Borde = Color.FromArgb(0x3A, 0x3F, 0x4A);
    static readonly Color Tenue = Color.FromArgb(0x8A, 0x8F, 0x98);
    static readonly Color Ambar = Color.FromArgb(0xF2, 0xB5, 0x3A);
    static readonly Color Azul = Color.FromArgb(0x6F, 0xA8, 0xFF);
    static readonly Color Verde = Color.FromArgb(0x5F, 0xD3, 0x8A);

    static GraphicsPath Redondeado(RectangleF r, float radio)
    {
        var p = new GraphicsPath();
        float d = radio * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Luz(Graphics g, float cx, float cy, float r, Color c, bool brillo)
    {
        if (brillo)
        {
            using (var halo = new GraphicsPath())
            {
                halo.AddEllipse(cx - r * 2.3f, cy - r * 2.3f, r * 4.6f, r * 4.6f);
                using (var b = new PathGradientBrush(halo))
                {
                    b.CenterColor = Color.FromArgb(120, c);
                    b.SurroundColors = new[] { Color.FromArgb(0, c) };
                    g.FillPath(b, halo);
                }
            }
        }
        using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        // Reflejo arriba a la izquierda: la luz parece una lámpara de cristal.
        using (var b = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
            g.FillEllipse(b, cx - r * 0.55f, cy - r * 0.6f, r * 0.55f, r * 0.45f);
    }

    public static byte[] Png(int n)
    {
        using (var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float m = n * 0.04f;
            var caja = new RectangleF(m, m, n - 2 * m, n - 2 * m);
            using (var forma = Redondeado(caja, n * 0.22f))
            {
                using (var b = new LinearGradientBrush(caja, FondoAlto, Fondo, 90f)) g.FillPath(b, forma);
                if (n >= 32)
                    using (var p = new Pen(Borde, Math.Max(1f, n / 64f))) g.DrawPath(p, forma);
            }
            bool pequeno = n < 32;
            float luzY = pequeno ? n * 0.5f : n * 0.72f;
            float r = pequeno ? n * 0.12f : n * 0.075f;
            float paso = pequeno ? n * 0.29f : n * 0.22f;
            if (!pequeno)
            {
                // Dial: arco de 180° con marcas y la aguja inclinada hacia el ámbar.
                float cx = n * 0.5f, cy = n * 0.52f, ra = n * 0.27f;
                using (var p = new Pen(Tenue, n * 0.035f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, cx - ra, cy - ra, ra * 2, ra * 2, 200, 140);
                using (var p = new Pen(Color.FromArgb(160, Tenue), n * 0.022f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        double a = (200 + i * 35) * Math.PI / 180;
                        float x1 = cx + (float)Math.Cos(a) * ra * 0.72f, y1 = cy + (float)Math.Sin(a) * ra * 0.72f;
                        float x2 = cx + (float)Math.Cos(a) * ra * 0.86f, y2 = cy + (float)Math.Sin(a) * ra * 0.86f;
                        g.DrawLine(p, x1, y1, x2, y2);
                    }
                }
                double aguja = 300 * Math.PI / 180;
                using (var p = new Pen(Ambar, n * 0.04f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, cx, cy, cx + (float)Math.Cos(aguja) * ra * 0.8f, cy + (float)Math.Sin(aguja) * ra * 0.8f);
                using (var b = new SolidBrush(Tenue)) g.FillEllipse(b, cx - n * 0.035f, cy - n * 0.035f, n * 0.07f, n * 0.07f);
            }
            Luz(g, n * 0.5f - paso, luzY, r, Ambar, !pequeno);
            Luz(g, n * 0.5f, luzY, r, Azul, !pequeno);
            Luz(g, n * 0.5f + paso, luzY, r, Verde, !pequeno);
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    /// <summary>ICO con una entrada PNG por tamaño (válido desde Windows Vista).</summary>
    public static void Ico(string ruta, int[] tamanos, string muestras)
    {
        var pngs = new byte[tamanos.Length][];
        for (int i = 0; i < tamanos.Length; i++)
        {
            pngs[i] = Png(tamanos[i]);
            if (!string.IsNullOrEmpty(muestras)) File.WriteAllBytes(Path.Combine(muestras, "icono-" + tamanos[i] + ".png"), pngs[i]);
        }
        using (var f = new BinaryWriter(File.Create(ruta)))
        {
            f.Write((ushort)0); f.Write((ushort)1); f.Write((ushort)tamanos.Length);
            int desplazamiento = 6 + 16 * tamanos.Length;
            for (int i = 0; i < tamanos.Length; i++)
            {
                int n = tamanos[i];
                f.Write((byte)(n >= 256 ? 0 : n)); f.Write((byte)(n >= 256 ? 0 : n));
                f.Write((byte)0); f.Write((byte)0); f.Write((ushort)1); f.Write((ushort)32);
                f.Write(pngs[i].Length); f.Write(desplazamiento);
                desplazamiento += pngs[i].Length;
            }
            foreach (var p in pngs) f.Write(p);
        }
    }
}
'@

$raiz = Split-Path -Parent $PSScriptRoot
if ($Muestras) { New-Item -ItemType Directory -Force $Muestras | Out-Null }
$ico = Join-Path $raiz 'src\Cabina\icono.ico'
[IconoCabina]::Ico($ico, [int[]](16, 20, 24, 32, 40, 48, 64, 128, 256), $Muestras)
Write-Host "Icono: $ico"
