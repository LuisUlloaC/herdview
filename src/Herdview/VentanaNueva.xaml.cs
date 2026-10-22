using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
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

        public VentanaNueva(IReadOnlyList<string> recientes, IReadOnlyList<Cuenta> cuentas, string? ultimaCuenta)
        {
            InitializeComponent();
            SourceInitialized += (_, __) => Win32.BarraOscura(new WindowInteropHelper(this).Handle);
            Recientes.ItemsSource = recientes;
            Carpeta.Text = recientes.Count > 0 ? recientes[0] : "";
            foreach (var c in cuentas)
            {
                var opcion = new RadioButton
                {
                    Style = (Style)FindResource("Pastilla"),
                    GroupName = "Cuenta",
                    Content = new TextBlock { Text = c.Nombre, FontSize = 12.5 },
                    Tag = c,
                    IsChecked = c.Nombre == ultimaCuenta,
                };
                _cuentas.Add(opcion);
                Cuentas.Children.Add(opcion);
            }
            if (_cuentas.Count > 0 && _cuentas.TrueForAll(r => r.IsChecked != true))
            {
                _cuentas[0].IsChecked = true;
            }
            Pedido.TextChanged += (_, __) =>
                PistaPedido.Visibility = Pedido.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Loaded += (_, __) =>
            {
                Pedido.Focus();
                Keyboard.Focus(Pedido);
            };
        }

        public string CarpetaElegida => Carpeta.Text.Trim().Trim('"');
        public Cuenta? CuentaElegida => _cuentas.Find(r => r.IsChecked == true)?.Tag as Cuenta;
        public string PedidoInicial => Pedido.Text;

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
            bool enCarpeta = ReferenceEquals(sender, Carpeta);
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
            if (!Directory.Exists(CarpetaElegida))
            {
                Error.Text = "Esa carpeta no existe.";
                Error.Visibility = Visibility.Visible;
                return;
            }
            if (CuentaElegida == null)
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
