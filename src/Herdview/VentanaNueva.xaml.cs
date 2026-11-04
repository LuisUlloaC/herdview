using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Herdview.Modelo;
using Forms = System.Windows.Forms;

namespace Herdview
{
    /// <summary>«C:\git\harena» → «harena» (para la lista de recientes).</summary>
    public sealed class NombreCarpeta : IValueConverter
    {
        public static readonly NombreCarpeta Uno = new NombreCarpeta();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            Path.GetFileName(((value as string) ?? "").TrimEnd('\\', '/'));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Diálogo de sesión nueva: carpeta (escrita, elegida o de las recientes), cuenta y primer pedido.</summary>
    public partial class VentanaNueva : Window
    {
        private readonly List<RadioButton> _cuentas = new List<RadioButton>();
        private readonly List<RadioButton> _origenes = new List<RadioButton>();
        private readonly List<RadioButton> _contenedores = new List<RadioButton>();
        private readonly RadioButton? _nueva;
        private readonly Func<IReadOnlyList<Contenedor>>? _contenedoresEnMarcha;
        private readonly string? _carpetaUsuario;
        private bool _contenedoresPedidos;

        public VentanaNueva(IReadOnlyList<string> recientes, IReadOnlyList<Cuenta> cuentas, string? ultimaCuenta,
            Func<IReadOnlyList<Contenedor>>? contenedoresEnMarcha = null, string? carpetaUsuario = null)
        {
            _contenedoresEnMarcha = contenedoresEnMarcha;
            _carpetaUsuario = carpetaUsuario;
            InitializeComponent();
            SourceInitialized += (_, __) => Win32.BarraOscura(new WindowInteropHelper(this).Handle);
            Recientes.ItemsSource = recientes;
            Carpeta.Text = recientes.Count > 0 ? recientes[0] : "";
            foreach (var c in cuentas)
            {
                var opcion = Pastilla("Cuenta", c);
                opcion.IsChecked = c.Nombre == ultimaCuenta;
                _cuentas.Add(opcion);
                Cuentas.Children.Add(opcion);

                var origen = Pastilla("CopiarDe", c);
                origen.IsChecked = c.Nombre == ultimaCuenta;
                _origenes.Add(origen);
                CopiarDe.Children.Add(origen);
            }
            if (_cuentas.Count > 0 && _cuentas.TrueForAll(r => r.IsChecked != true))
            {
                _cuentas[0].IsChecked = true;
                _origenes[0].IsChecked = true;
            }
            var sinCopiar = Pastilla("CopiarDe", null, "nada");
            sinCopiar.ToolTip = "Empezar de cero, sin ajustes";
            sinCopiar.IsChecked = _origenes.Count == 0;
            CopiarDe.Children.Add(sinCopiar);
            _nueva = Pastilla("Cuenta", null, "+ nueva");
            _nueva.ToolTip = "Crear otra cuenta de Claude Code (.claude-<nombre>)";
            _nueva.Checked += (_, __) => PonerCuentaNueva(true);
            _nueva.Unchecked += (_, __) => PonerCuentaNueva(false);
            _nueva.IsChecked = _cuentas.Count == 0;
            Cuentas.Children.Add(_nueva);
            NombreCuenta.TextChanged += (_, __) => ErrorCuenta.Visibility = Visibility.Collapsed;
            Pedido.TextChanged += (_, __) =>
                PistaPedido.Visibility = Pedido.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Loaded += (_, __) =>
            {
                Control foco = CuentaNueva != null ? NombreCuenta : Pedido;
                foco.Focus();
                Keyboard.Focus(foco);
            };
        }

        /// <summary>Pastilla de elegir (cuenta, origen de los ajustes…); las cuentas sin sesión iniciada lo dicen.</summary>
        private RadioButton Pastilla(string grupo, Cuenta? cuenta, string? texto = null)
        {
            var etiqueta = new TextBlock { FontSize = 12.5 };
            etiqueta.Inlines.Add(new Run(texto ?? cuenta?.Nombre ?? ""));
            bool avisar = grupo == "Cuenta" && cuenta?.SinSesion == true;
            if (avisar)
            {
                etiqueta.Inlines.Add(new Run(" · sin sesión") { FontSize = 11, Foreground = (Brush)FindResource("BrApagada") });
            }
            return new RadioButton
            {
                Style = (Style)FindResource("Pastilla"),
                GroupName = grupo,
                Content = etiqueta,
                Tag = cuenta,
                ToolTip = avisar ? "Aún no has iniciado sesión en esta cuenta: al abrirla, Claude te pedirá entrar." : null,
            };
        }

        private void PonerCuentaNueva(bool nueva)
        {
            PanelCuentaNueva.Visibility = nueva ? Visibility.Visible : Visibility.Collapsed;
            PonerBoton();
            if (nueva && IsLoaded)
            {
                NombreCuenta.Focus();
            }
        }

        /// <summary>Crear una cuenta (solo en Windows) cambia el botón y quita el primer pedido.</summary>
        private void PonerBoton()
        {
            bool crear = CuentaNueva != null;
            BloquePedido.Visibility = crear ? Visibility.Collapsed : Visibility.Visible;
            BotonAbrir.Content = crear ? "Crear y abrir" : "Abrir en Windows Terminal";
        }

        /// <summary>Con «+ nueva» elegida y este nombre (la captura de pantalla lo usa).</summary>
        public void MostrarCuentaNueva(string nombre)
        {
            if (_nueva != null)
            {
                _nueva.IsChecked = true;
            }
            NombreCuenta.Text = nombre;
        }

        public string CarpetaElegida => Carpeta.Text.Trim().Trim('"');
        public Cuenta? CuentaElegida => _cuentas.Find(r => r.IsChecked == true)?.Tag as Cuenta;
        /// <summary>El nombre de la cuenta a crear (null si se eligió una que ya existe).</summary>
        public string? CuentaNueva =>
            EnContenedor.IsChecked != true && _nueva?.IsChecked == true ? NombreCuenta.Text.Trim().ToLowerInvariant() : null;
        /// <summary>De qué cuenta copiar los ajustes de la nueva (null = empezar de cero).</summary>
        public Cuenta? CopiarAjustesDe => _origenes.Find(r => r.IsChecked == true)?.Tag as Cuenta;
        /// <summary>Una cuenta nueva arranca sin pedido: antes viene la bienvenida de Claude y el inicio de sesión.</summary>
        public string PedidoInicial => CuentaNueva != null ? "" : Pedido.Text;
        /// <summary>El contenedor elegido (null si la sesión va en Windows).</summary>
        public Contenedor? ContenedorElegido =>
            EnContenedor.IsChecked == true ? _contenedores.Find(r => r.IsChecked == true)?.Tag as Contenedor : null;
        public string CarpetaContenedor => CarpetaDocker.Text.Trim();

        private void AlCambiarDonde(object sender, RoutedEventArgs e)
        {
            if (PanelDocker == null || PanelWindows == null)
            {
                return; // aún construyéndose
            }
            bool docker = EnContenedor.IsChecked == true;
            PanelDocker.Visibility = docker ? Visibility.Visible : Visibility.Collapsed;
            PanelWindows.Visibility = docker ? Visibility.Collapsed : Visibility.Visible;
            Error.Visibility = Visibility.Collapsed;
            PonerBoton();
            if (docker && !_contenedoresPedidos)
            {
                _contenedoresPedidos = true;
                CargarContenedores();
            }
        }

        /// <summary>docker ps en segundo plano (con Docker parado tarda un poco en fallar).</summary>
        private void CargarContenedores()
        {
            EstadoDocker.Text = "Buscando contenedores…";
            var consulta = _contenedoresEnMarcha;
            System.Threading.Tasks.Task.Run(() => consulta?.Invoke() ?? Array.Empty<Contenedor>()).ContinueWith(t =>
            {
                PonerContenedores(t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : Array.Empty<Contenedor>());
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Pasa a Docker con estos contenedores (la captura de pantalla lo usa sin preguntar a Docker).</summary>
        public void MostrarDocker(IReadOnlyList<Contenedor> lista)
        {
            _contenedoresPedidos = true;
            EnContenedor.IsChecked = true;
            PonerContenedores(lista);
        }

        private void PonerContenedores(IReadOnlyList<Contenedor> lista)
        {
                EstadoDocker.Text = lista.Count == 0 ? "No hay contenedores en marcha (¿está arrancado Docker Desktop?)." : "";
                EstadoDocker.Visibility = lista.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var c in lista)
                {
                    var opcion = new RadioButton
                    {
                        Style = (Style)FindResource("Pastilla"),
                        GroupName = "Contenedor",
                        Content = new TextBlock { Text = c.Nombre, FontSize = 12.5 },
                        ToolTip = c.Imagen,
                        Tag = c,
                        Margin = new Thickness(0, 0, 6, 6),
                    };
                    _contenedores.Add(opcion);
                    Contenedores.Children.Add(opcion);
                }
                if (_contenedores.Count > 0)
                {
                    _contenedores[0].IsChecked = true;
                }
        }

        private void AlElegir(object sender, RoutedEventArgs e)
        {
            using (var dialogo = new Forms.FolderBrowserDialog
            {
                Description = "Carpeta del proyecto",
                SelectedPath = Directory.Exists(CarpetaElegida) ? CarpetaElegida : "",
                ShowNewFolderButton = true,
            })
            {
                if (dialogo.ShowDialog() == Forms.DialogResult.OK)
                {
                    Carpeta.Text = dialogo.SelectedPath;
                }
            }
        }

        private void AlElegirReciente(object sender, RoutedEventArgs e)
        {
            Carpeta.Text = (string)((Button)sender).Tag;
            Pedido.Focus();
        }

        private void AlTeclear(object sender, KeyEventArgs e)
        {
            bool enCarpeta = ReferenceEquals(sender, Carpeta) || ReferenceEquals(sender, NombreCuenta);
            if (e.Key == Key.Enter && (enCarpeta || (Keyboard.Modifiers & ModifierKeys.Control) != 0))
            {
                AlAbrir(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private void AlAbrir(object sender, RoutedEventArgs e)
        {
            if (EnContenedor.IsChecked == true)
            {
                if (ContenedorElegido == null)
                {
                    EstadoDocker.Text = "Elige un contenedor en marcha.";
                    EstadoDocker.Visibility = Visibility.Visible;
                    return;
                }
                DialogResult = true;
                return;
            }
            if (!Directory.Exists(CarpetaElegida))
            {
                Error.Text = "Esa carpeta no existe.";
                Error.Visibility = Visibility.Visible;
                return;
            }
            if (CuentaNueva != null)
            {
                string? error = AltaCuenta.ErrorNombre(_carpetaUsuario, CuentaNueva);
                if (error != null)
                {
                    ErrorCuenta.Text = error;
                    ErrorCuenta.Visibility = Visibility.Visible;
                    NombreCuenta.Focus();
                    return;
                }
            }
            else if (CuentaElegida == null)
            {
                Error.Text = "No encuentro ninguna cuenta de Claude Code (.claude o .claude-*).";
                Error.Visibility = Visibility.Visible;
                return;
            }
            DialogResult = true;
        }

        private void AlCancelar(object sender, RoutedEventArgs e) => Close();
    }
}
