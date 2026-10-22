using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Cabina.Modelo;
using Forms = System.Windows.Forms;

namespace Cabina
{
    /// <summary>Junta todo: lee las sesiones que anota el hook, las pinta en la tira, el panel y la bandeja, y al hacer
    /// clic en una trae su ventana. Una sola instancia.</summary>
    public partial class App : Application
    {
        private static readonly TimeSpan CadaCuantoComprobar = TimeSpan.FromSeconds(5);

        private readonly ObservableCollection<SesionVM> _todas = new ObservableCollection<SesionVM>();
        /// <summary>Id → «desde» del estado que viste al hacer clic: lo terminado que ya viste se apaga.</summary>
        private readonly Dictionary<string, double> _vistas = new Dictionary<string, double>();
        private readonly IConsultaProcesos _procesos = new ConsultaProcesosWindows();

        private Mutex? _unica;
        private AlmacenSesiones? _almacen;
        private Ajustes _ajustes = new Ajustes();
        private VentanaTira? _tira;
        private VentanaPanel? _panel;
        private Forms.NotifyIcon? _bandeja;
        private EstadoSesion? _global;
        private bool _globalPintado;
        private DispatcherTimer? _rebote;
        private DateTime _ultimaCarga = DateTime.MinValue;
        private readonly Avisador _avisador = new Avisador();
        /// <summary>La sesión del último aviso: al pulsarlo se abre el panel listo para escribirle.</summary>
        private string? _sesionDelAviso;

        private void AlArrancar(object sender, StartupEventArgs e)
        {
            if (e.Args.Length == 2 && e.Args[0] == "--captura")
            {
                Captura.Hacer(e.Args[1]);
                Shutdown();
                return;
            }
            _unica = new Mutex(true, @"Local\Cabina.Claude", out bool primera);
            if (!primera)
            {
                Shutdown();
                return;
            }

            _ajustes = Ajustes.Cargar();
            _tira = new VentanaTira(_todas);
            _tira.AsaPulsada += (_, __) => AlternarPanel();
            _tira.Movida += (_, __) =>
            {
                _ajustes.TiraX = _tira.Left;
                _ajustes.TiraY = _tira.Top;
                _ajustes.Guardar();
            };
            _panel = new VentanaPanel(_todas);
            _panel.CambiarTira += (_, __) => CambiarTira();
            _panel.CambiarArranque += (_, __) =>
            {
                Ajustes.ArrancaConWindows = !Ajustes.ArrancaConWindows;
                Pintar();
            };
            _panel.Salir += (_, __) => Shutdown();

            CrearBandeja();

            _almacen = new AlmacenSesiones(AlmacenSesiones.CarpetaPorDefecto, _procesos);
            _rebote = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _rebote.Tick += (_, __) =>
            {
                _rebote.Stop();
                Recargar();
            };
            _almacen.Cambio += (_, __) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _rebote.Stop();
                _rebote.Start();
            }));
            _almacen.Vigilar();

            var reloj = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            reloj.Tick += (_, __) => Tic();
            reloj.Start();

            Recargar();
        }

        private void AlSalir(object sender, ExitEventArgs e)
        {
            if (_bandeja != null)
            {
                _bandeja.Visible = false;
                _bandeja.Dispose();
            }
            _almacen?.Dispose();
            _unica?.Dispose();
        }

        private bool Vista(Sesion s) => _vistas.TryGetValue(s.Id, out double desde) && desde >= s.Desde;

        private void Tic()
        {
            DateTime ahora = DateTime.UtcNow;
            if (ahora - _ultimaCarga >= CadaCuantoComprobar)
            {
                Recargar();
                return;
            }
            foreach (var vm in _todas)
            {
                vm.Tic(ahora);
            }
        }

        private void Recargar()
        {
            if (_almacen == null)
            {
                return;
            }
            DateTime ahora = DateTime.UtcNow;
            _ultimaCarga = ahora;
            var anotadas = _almacen.Cargar(ahora);
            anotadas.AddRange(Procesos.SinDatos(anotadas, _procesos,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            var sesiones = Panorama.Ordenar(anotadas, Vista);
            if (_panel != null && _panel.IsKeyboardFocusWithin)
            {
                // Escribiendo en el panel: mover filas regeneraría la caja y se perdería lo escrito. Se reordena al salir.
                var orden = _todas.Select((vm, i) => (vm.Id, i)).ToDictionary(x => x.Id, x => x.i);
                sesiones = sesiones.OrderBy(s => orden.TryGetValue(s.Id, out int i) ? i : int.MaxValue).ToList();
            }
            Sincronizar(sesiones, ahora);
            foreach (string id in _vistas.Keys.Where(id => sesiones.All(s => s.Id != id)).ToList())
            {
                _vistas.Remove(id);
            }
            _global = Panorama.Global(sesiones, Vista);
            Pintar();
            foreach (var s in _avisador.QueAvisar(sesiones, ahora))
            {
                Notificar(s);
            }
        }

        /// <summary>Deja la colección con las sesiones dadas en su orden, reutilizando los VM (no parpadea).</summary>
        private void Sincronizar(List<Sesion> sesiones, DateTime ahora)
        {
            var coleccion = _todas;
            for (int i = 0; i < sesiones.Count; i++)
            {
                var s = sesiones[i];
                int j = IndiceDe(coleccion, s.Id, i);
                SesionVM vm;
                if (j < 0)
                {
                    vm = new SesionVM(s, Enfocar, Enviar);
                    coleccion.Insert(i, vm);
                }
                else
                {
                    vm = coleccion[j];
                    if (j != i)
                    {
                        coleccion.Move(j, i);
                    }
                }
                vm.Actualizar(s, Vista(s), ahora);
            }
            while (coleccion.Count > sesiones.Count)
            {
                coleccion.RemoveAt(coleccion.Count - 1);
            }
        }

        private static int IndiceDe(ObservableCollection<SesionVM> coleccion, string id, int desde)
        {
            for (int k = desde; k < coleccion.Count; k++)
            {
                if (coleccion[k].Id == id)
                {
                    return k;
                }
            }
            return -1;
        }

        private void Enfocar(SesionVM vm)
        {
            _vistas[vm.Id] = vm.Sesion.Desde;
            IntPtr ventana = Procesos.VentanaDe(vm.Sesion, _procesos);
            if (ventana != IntPtr.Zero)
            {
                _panel?.Hide();
                Win32.Enfocar(ventana);
            }
            Recargar();
        }

        /// <summary>Notificación de Windows (desde el icono de la bandeja). Si ya estás en la ventana de esa sesión, no.</summary>
        private void Notificar(Sesion s)
        {
            if (_bandeja == null)
            {
                return;
            }
            IntPtr suya = Procesos.VentanaDe(s, _procesos);
            if (suya != IntPtr.Zero && suya == Win32.Primera())
            {
                return;
            }
            string proyecto = string.IsNullOrEmpty(s.Proyecto) ? "Claude" : s.Proyecto;
            bool espera = s.Estado == EstadoSesion.Esperando;
            string titulo = proyecto + (espera ? " te espera" : " terminó");
            string texto = espera ? (s.Aviso.Length > 0 ? s.Aviso : "Necesita tu atención")
                : (s.Pedido.Length > 0 ? "» " + s.Pedido : "Listo para seguir");
            texto += "\nPulsa para contestarle";
            _sesionDelAviso = s.Id;
            _bandeja.ShowBalloonTip(8000, titulo, texto.Length > 250 ? texto.Substring(0, 249) + "…" : texto,
                espera ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }

        private void AlPulsarAviso()
        {
            if (_panel == null || _sesionDelAviso == null)
            {
                return;
            }
            Recargar();
            _panel.Abrir(_todas.FirstOrDefault(v => v.Id == _sesionDelAviso));
        }

        private bool Enviar(SesionVM vm, string texto)
        {
            try
            {
                Buzon.Enviar(Buzon.CarpetaPorDefecto, vm.Id, texto);
                return true;
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void Pintar()
        {
            if (_tira == null || _panel == null || _bandeja == null)
            {
                return;
            }
            bool verTira = _ajustes.TiraVisible;
            _tira.PonerVacia(_todas.Count == 0);
            if (verTira && !_tira.IsVisible)
            {
                _tira.Show();
                _tira.Colocar(_ajustes.TiraX, _ajustes.TiraY);
            }
            else if (!verTira && _tira.IsVisible)
            {
                _tira.Hide();
            }

            string resumen = Resumen();
            _panel.Pintar(resumen, _todas.Count > 0, _ajustes.TiraVisible, Ajustes.ArrancaConWindows);
            if (!_globalPintado || _bandeja.Tag as EstadoSesion? != _global)
            {
                var viejo = _bandeja.Icon;
                _bandeja.Icon = IconoBandeja.Crear(_global);
                _bandeja.Tag = _global;
                viejo?.Dispose();
                _globalPintado = true;
            }
            string texto = "Cabina · " + resumen;
            _bandeja.Text = texto.Length > 63 ? texto.Substring(0, 63) : texto;
        }

        private string Resumen()
        {
            int espera = _todas.Count(v => v.Estado == EstadoSesion.Esperando);
            int trabajo = _todas.Count(v => v.Estado == EstadoSesion.Trabajando);
            int listas = _todas.Count(v => v.Estado == EstadoSesion.Terminado);
            var partes = new List<string>();
            if (espera > 0) partes.Add(espera == 1 ? "1 te espera" : $"{espera} te esperan");
            if (trabajo > 0) partes.Add($"{trabajo} trabajando");
            if (listas > 0) partes.Add(listas == 1 ? "1 terminó" : $"{listas} terminaron");
            if (partes.Count == 0) return _todas.Count == 0 ? "Sin sesiones" : "Todo tranquilo";
            return string.Join(" · ", partes);
        }

        private void CambiarTira()
        {
            _ajustes.TiraVisible = !_ajustes.TiraVisible;
            _ajustes.Guardar();
            Pintar();
        }

        private void AlternarPanel()
        {
            if (_panel == null)
            {
                return;
            }
            if (_panel.IsVisible)
            {
                _panel.Hide();
            }
            else if (DateTime.UtcNow - _panel.OcultadoEn > TimeSpan.FromMilliseconds(400))
            {
                Recargar();
                _panel.Abrir();
            }
        }

        private void CrearBandeja()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Abrir panel", null, (_, __) => AlternarPanel());
            var tira = new Forms.ToolStripMenuItem("Tira visible", null, (_, __) => CambiarTira());
            var arranque = new Forms.ToolStripMenuItem("Arrancar con Windows", null, (_, __) =>
            {
                Ajustes.ArrancaConWindows = !Ajustes.ArrancaConWindows;
                Pintar();
            });
            menu.Items.Add(tira);
            menu.Items.Add(arranque);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Salir", null, (_, __) => Shutdown());
            menu.Opening += (_, __) =>
            {
                tira.Checked = _ajustes.TiraVisible;
                arranque.Checked = Ajustes.ArrancaConWindows;
            };

            _bandeja = new Forms.NotifyIcon
            {
                Icon = IconoBandeja.Crear(null),
                Text = "Cabina",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _bandeja.BalloonTipClicked += (_, __) => AlPulsarAviso();
            _bandeja.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    AlternarPanel();
                }
            };
        }
    }
}
