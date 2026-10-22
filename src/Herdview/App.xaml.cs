using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Herdview.Modelo;
using Forms = System.Windows.Forms;

namespace Herdview
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
        private VentanaSesiones? _ventana;
        /// <summary>Lee las transcripciones (solo lo nuevo cada vez). Lo usa una sola tarea a la vez.</summary>
        private readonly MedidorConsumo _medidor = new MedidorConsumo();
        private bool _midiendo;
        private Recientes _recientes = new Recientes();
        private readonly IDocker _docker = new DockerCli();
        /// <summary>Las sesiones de los contenedores, de la última consulta a Docker (en segundo plano).</summary>
        private List<Sesion> _sesionesDocker = new List<Sesion>();
        private bool _consultandoDocker;
        /// <summary>Sesiones que se están cerrando: se esconden ya, sin esperar a que su fichero desaparezca.</summary>
        private readonly HashSet<string> _cerradas = new HashSet<string>();
        private DateTime _ultimaDocker = DateTime.MinValue;

        private void AlArrancar(object sender, StartupEventArgs e)
        {
            if (e.Args.Length == 2 && e.Args[0] == "--captura")
            {
                Captura.Hacer(e.Args[1]);
                Shutdown();
                return;
            }
            _unica = new Mutex(true, @"Local\Herdview.Claude", out bool primera);
            if (!primera)
            {
                Shutdown();
                return;
            }

            _ajustes = Ajustes.Cargar();
            _recientes = new Recientes(_ajustes.Recientes);
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
            _panel.AbrirVentana += (_, __) => AbrirVentana();
            _panel.NuevaSesion += (_, __) => NuevaSesion();
            _ventana = new VentanaSesiones(_todas);
            _ventana.NuevaSesion += (_, __) => NuevaSesion();

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
            _ventana?.CerrarDeVerdad();
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
            anotadas.AddRange(_sesionesDocker);
            ConsultarDocker(ahora);
            anotadas.AddRange(Procesos.SinDatos(anotadas, _procesos,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            _cerradas.RemoveWhere(id => anotadas.All(s => s.Id != id));
            var sesiones = Panorama.Ordenar(anotadas.Where(s => !_cerradas.Contains(s.Id)), Vista);
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
            Medir();
            bool nuevas = false;
            foreach (var s in sesiones.Where(s => s.Cwd.Length > 0))
            {
                nuevas |= _recientes.Conocer(s.Cwd);
            }
            if (nuevas)
            {
                GuardarRecientes();
            }
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
                    vm = new SesionVM(s, Enfocar, Enviar, Cerrar);
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
                if (s.ConsumoHook != null)
                {
                    vm.PonerConsumo(s.ConsumoHook.AConsumo());
                }
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
            if (ventana == IntPtr.Zero && vm.Sesion.Contenedor.Length > 0)
            {
                ventana = Procesos.VentanaPorTitulo(_procesos, new[] { vm.Sesion.Contenedor, vm.Proyecto });
            }
            if (ventana != IntPtr.Zero)
            {
                _panel?.Hide();
                Win32.Enfocar(ventana);
            }
            Recargar();
        }

        /// <summary>Cada 5 s, en segundo plano, las sesiones de los contenedores (docker exec tarda; con Docker parado no
        /// hay ninguna). Si cambia algo, se vuelve a pintar.</summary>
        private void ConsultarDocker(DateTime ahora)
        {
            if (_consultandoDocker || ahora - _ultimaDocker < CadaCuantoComprobar)
            {
                return;
            }
            _consultandoDocker = true;
            _ultimaDocker = ahora;
            System.Threading.Tasks.Task.Run(() => EnDocker.Sesiones(_docker)).ContinueWith(t =>
            {
                _consultandoDocker = false;
                var nuevas = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : new List<Sesion>();
                bool cambio = nuevas.Count != _sesionesDocker.Count
                    || nuevas.Zip(_sesionesDocker, (a, b) => a.Id != b.Id || a.Ts != b.Ts).Any(x => x);
                _sesionesDocker = nuevas;
                if (cambio)
                {
                    Recargar();
                }
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static string HookIncrustado()
        {
            using (var flujo = typeof(App).Assembly.GetManifestResourceStream("herdview.py")!)
            using (var lector = new StreamReader(flujo, System.Text.Encoding.UTF8))
            {
                return lector.ReadToEnd().Replace("\r\n", "\n");
            }
        }

        private void GuardarRecientes()
        {
            _ajustes.Recientes = _recientes.Carpetas.ToList();
            _ajustes.Guardar();
        }

        /// <summary>Diálogo de sesión nueva y, si se acepta, pestaña de Windows Terminal con Claude arrancado.</summary>
        private void NuevaSesion()
        {
            _panel?.Hide();
            var cuentas = Lanzador.Cuentas(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var dialogo = new VentanaNueva(_recientes.Carpetas.ToList(), cuentas, _ajustes.UltimaCuenta, () => _docker.EnMarcha());
            if (_ventana != null && _ventana.IsVisible)
            {
                dialogo.Owner = _ventana;
            }
            if (dialogo.ShowDialog() != true)
            {
                return;
            }
            if (dialogo.ContenedorElegido != null)
            {
                var c = dialogo.ContenedorElegido;
                var (resultado, detalle) = EnDocker.Preparar(_docker, c.Id, HookIncrustado());
                if (resultado != EnDocker.Preparacion.Lista)
                {
                    MessageBox.Show(detalle, "Herdview · " + c.Nombre, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                AbrirTerminal(EnDocker.ArgumentosTerminal(c.Nombre, dialogo.CarpetaContenedor, dialogo.PedidoInicial), null,
                    "-NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                        EnDocker.Script(c.Nombre, dialogo.CarpetaContenedor, dialogo.PedidoInicial))));
                return;
            }
            if (dialogo.CuentaElegida == null)
            {
                return;
            }
            Lanzar(dialogo.CarpetaElegida, dialogo.CuentaElegida, dialogo.PedidoInicial);
            _recientes.Usar(dialogo.CarpetaElegida);
            _ajustes.UltimaCuenta = dialogo.CuentaElegida.Nombre;
            GuardarRecientes();
        }

        private static void Lanzar(string carpeta, Cuenta cuenta, string pedido) =>
            AbrirTerminal(Lanzador.ArgumentosTerminal(carpeta, cuenta, pedido), carpeta, Lanzador.ArgumentosPowerShell(cuenta, pedido));

        /// <summary>Pestaña nueva de Windows Terminal; sin él, una consola de PowerShell con los mismos argumentos.</summary>
        private static void AbrirTerminal(string argumentosWt, string? carpeta, string argumentosPowerShell)
        {
            string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");
            try
            {
                if (File.Exists(wt))
                {
                    Process.Start(new ProcessStartInfo(wt, argumentosWt)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = carpeta ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    });
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Sin Windows Terminal utilizable: una consola normal.
            }
            Process.Start(new ProcessStartInfo("powershell.exe", argumentosPowerShell)
            {
                UseShellExecute = true,
                WorkingDirectory = carpeta ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
        }

        private void AbrirVentana()
        {
            if (_ventana == null)
            {
                return;
            }
            _panel?.Hide();
            _ventana.Abrir();
            Medir();
        }

        /// <summary>Consumo de tokens en segundo plano (la primera lectura de una sesión larga tarda segundos). Solo con la
        /// ventana abierta, que es donde se ve; al volver pone las cifras en cada sesión.</summary>
        private void Medir()
        {
            if (_midiendo || _ventana == null || !_ventana.IsVisible)
            {
                return;
            }
            var pendientes = _todas.Where(v => v.Sesion.Transcript.Length > 0)
                .Select(v => (v.Id, v.Sesion.Transcript)).ToList();
            _midiendo = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                var medidas = new Dictionary<string, Consumo>();
                foreach (var (id, transcript) in pendientes)
                {
                    if (System.IO.File.Exists(transcript))
                    {
                        medidas[id] = _medidor.Medir(transcript);
                    }
                }
                return medidas;
            }).ContinueWith(t =>
            {
                _midiendo = false;
                if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
                {
                    return;
                }
                foreach (var vm in _todas)
                {
                    if (t.Result.TryGetValue(vm.Id, out var c))
                    {
                        vm.PonerConsumo(c);
                    }
                }
                _ventana?.Pintar(Resumen(), _todas);
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
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

        /// <summary>Cierra la sesión tras confirmarlo: en Windows con Ctrl+C a su consola (y a la fuerza si no sale), en
        /// un contenedor con kill -TERM (y -KILL). Se hace en segundo plano y la sesión desaparece de la lista al acabar.</summary>
        private void Cerrar(SesionVM vm)
        {
            var s = vm.Sesion;
            string donde = s.Contenedor.Length > 0 ? "en el contenedor " + s.Contenedor : vm.Origen;
            string aviso = s.Estado == EstadoSesion.Trabajando ? "\n\nEstá trabajando: se cortará lo que esté haciendo." : "";
            if (MessageBox.Show("¿Cerrar la sesión «" + vm.Proyecto + "» (" + donde + ")?" + aviso, "Herdview",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }
            int pid = s.Contenedor.Length > 0 ? 0 : Procesos.PidClaude(s, _procesos.Tabla());
            if (s.Contenedor.Length == 0 && pid == 0)
            {
                MessageBox.Show("No encuentro el proceso de esa sesión: puede que ya se haya cerrado.", "Herdview");
                return;
            }
            vm.PonerNota("Cerrando…");
            System.Threading.Tasks.Task.Run(() =>
            {
                if (s.Contenedor.Length > 0)
                {
                    return EnDocker.Cerrar(_docker, s) ? "cerrada" : "no se pudo cerrar";
                }
                return Cerrador.Cerrar(pid);
            }).ContinueWith(t =>
            {
                string resultado = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : "no se pudo cerrar";
                if (resultado.StartsWith("no", StringComparison.Ordinal))
                {
                    vm.PonerNota("No se pudo cerrar");
                    return;
                }
                _cerradas.Add(s.Id);
                if (s.Contenedor.Length == 0)
                {
                    try
                    {
                        File.Delete(Path.Combine(AlmacenSesiones.CarpetaPorDefecto, s.Id + ".json"));
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                    }
                }
                _ultimaDocker = DateTime.MinValue;
                Recargar();
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private bool Enviar(SesionVM vm, string texto)
        {
            if (vm.Sesion.Contenedor.Length > 0)
            {
                return EnDocker.Enviar(_docker, vm.Sesion, texto);
            }
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
            _ventana?.Pintar(resumen, _todas);
            _panel.Pintar(resumen, _todas.Count > 0, _ajustes.TiraVisible, Ajustes.ArrancaConWindows);
            if (!_globalPintado || _bandeja.Tag as EstadoSesion? != _global)
            {
                var viejo = _bandeja.Icon;
                _bandeja.Icon = IconoBandeja.Crear(_global);
                _bandeja.Tag = _global;
                viejo?.Dispose();
                _globalPintado = true;
            }
            string texto = "Herdview · " + resumen;
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
            menu.Items.Add("Abrir ventana (con tokens)", null, (_, __) => AbrirVentana());
            menu.Items.Add("Nueva sesión de Claude…", null, (_, __) => NuevaSesion());
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
                Text = "Herdview",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _bandeja.BalloonTipClicked += (_, __) => AlPulsarAviso();
            _bandeja.MouseDoubleClick += (_, __) => AbrirVentana();
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
