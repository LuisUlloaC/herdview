using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cabina.Modelo;

namespace Cabina
{
    /// <summary>`Cabina.exe --captura &lt;carpeta&gt;`: pinta la tira y el panel con sesiones de ejemplo a PNG, sin abrir
    /// ninguna ventana (para revisar el diseño sin abrir nada en pantalla).</summary>
    internal static class Captura
    {
        public static void Hacer(string carpeta)
        {
            Directory.CreateDirectory(carpeta);
            DateTime ahora = DateTime.UtcNow;
            double Hace(double segundos) => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0) - segundos;
            var ejemplos = new[]
            {
                new Sesion { Id = "1", Proyecto = "api-pagos", Origen = "VS Code", EstadoTexto = "esperando", Desde = Hace(42),
                    Aviso = "Pide permiso para usar Bash", Pedido = "revisa por qué el informe de ayer no avisó de los pagos rechazados" },
                new Sesion { Id = "2", Proyecto = "juego-3d", Origen = "Terminal", EstadoTexto = "trabajando", Desde = Hace(187),
                    Actividad = "Renderizar hojas de revisión del andar de lado", Pedido = "sigue con los lados y las diagonales" },
                new Sesion { Id = "3", Proyecto = "tienda-web", Origen = "Terminal", EstadoTexto = "terminado", Desde = Hace(260),
                    Pedido = "arregla el carrito en móvil" },
                new Sesion { Id = "4", Proyecto = "blog", Origen = "VS Code", EstadoTexto = "inicio", Desde = Hace(3600 * 3) },
            };
            var todas = new ObservableCollection<SesionVM>();
            foreach (var s in ejemplos)
            {
                var vm = new SesionVM(s, _ => { }, (_, __) => true);
                vm.Actualizar(s, false, ahora);
                todas.Add(vm);
            }
            var tira = new VentanaTira(todas);
            Guardar((FrameworkElement)tira.Content, Path.Combine(carpeta, "tira.png"), double.PositiveInfinity);

            var panel = new VentanaPanel(todas);
            panel.Pintar("1 te espera · 1 trabajando · 1 terminó", true, true, false);
            Guardar((FrameworkElement)panel.Content, Path.Combine(carpeta, "panel.png"), panel.Width,
                (Brush)Application.Current.Resources["BrFondo"]);
        }

        private static void Guardar(FrameworkElement elemento, string ruta, double ancho, Brush? fondo = null)
        {
            const double Escala = 2;
            elemento.Measure(new Size(ancho, double.PositiveInfinity));
            var tam = elemento.DesiredSize;
            elemento.Arrange(new Rect(tam));
            elemento.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(tam.Width * Escala), (int)Math.Ceiling(tam.Height * Escala),
                96 * Escala, 96 * Escala, PixelFormats.Pbgra32);
            if (fondo != null)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(fondo, null, new Rect(tam));
                }
                bmp.Render(dv);
            }
            bmp.Render(elemento);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(ruta))
            {
                png.Save(f);
            }
        }
    }
}
