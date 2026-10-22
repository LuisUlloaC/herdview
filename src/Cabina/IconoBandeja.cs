using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Cabina.Modelo;

namespace Cabina
{
    /// <summary>El icono de la bandeja es una luz de la cabina: del color del estado más urgente, apagada si no hay nada.</summary>
    internal static class IconoBandeja
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Color ColorDe(EstadoSesion? estado)
        {
            switch (estado)
            {
                case EstadoSesion.Esperando: return Color.FromArgb(0xF2, 0xB5, 0x3A);
                case EstadoSesion.Trabajando: return Color.FromArgb(0x6F, 0xA8, 0xFF);
                case EstadoSesion.Terminado: return Color.FromArgb(0x5F, 0xD3, 0x8A);
                default: return Color.FromArgb(0x8A, 0x8F, 0x98);
            }
        }

        public static Icon Crear(EstadoSesion? estado)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var marco = new SolidBrush(Color.FromArgb(0x16, 0x18, 0x1C)))
                {
                    g.FillEllipse(marco, 1, 1, 30, 30);
                }
                var color = ColorDe(estado);
                if (estado == null)
                {
                    using (var aro = new Pen(color, 3))
                    {
                        g.DrawEllipse(aro, 8, 8, 16, 16);
                    }
                }
                else
                {
                    using (var luz = new SolidBrush(color))
                    {
                        g.FillEllipse(luz, 6, 6, 20, 20);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(h).Clone();
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
        }
    }
}
