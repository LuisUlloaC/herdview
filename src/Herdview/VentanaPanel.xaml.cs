using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Herdview
{
    /// <summary>El panel completo, como un menú de la bandeja: se abre junto al reloj y se esconde al perder el foco.</summary>
    public partial class VentanaPanel : Window
    {
        public VentanaPanel(ObservableCollection<SesionVM> sesiones)
        {
            InitializeComponent();
            Lista.ItemsSource = sesiones;
            Deactivated += (_, __) =>
            {
                OcultadoEn = DateTime.UtcNow;
                Hide();
            };
            // Crece hacia arriba: el borde de abajo se queda pegado a la barra de tareas.
            SizeChanged += (_, __) =>
            {
                if (IsVisible)
                {
                    Top = SystemParameters.WorkArea.Bottom - ActualHeight - 10;
                }
            };
        }

        /// <summary>Cuándo se escondió por perder el foco: el clic en la bandeja que lo provoca no debe reabrirlo.</summary>
        public DateTime OcultadoEn { get; private set; }

        public event EventHandler? CambiarTira;
        public event EventHandler? CambiarArranque;
        public event EventHandler? Salir;
        public event EventHandler? AbrirVentana;

        public void Pintar(string resumen, bool hay, bool tiraVisible, bool arranca)
        {
            Resumen.Text = resumen;
            Vacio.Visibility = hay ? Visibility.Collapsed : Visibility.Visible;
            TextoTira.Text = tiraVisible ? "Ocultar tira" : "Mostrar tira";
            TextoArranque.Text = arranca ? "✓ Arranca con Windows" : "Arrancar con Windows";
        }

        /// <summary>Abajo a la derecha, encima del reloj (o donde esté la barra de tareas).</summary>
        public void Abrir(SesionVM? escribirA = null)
        {
            Show();
            UpdateLayout();
            Rect area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 10;
            Top = area.Bottom - ActualHeight - 10;
            Activate();
            if (escribirA != null && Lista.ItemContainerGenerator.ContainerFromItem(escribirA) is ContentPresenter fila)
            {
                fila.ApplyTemplate();
                if (fila.ContentTemplate.FindName("Caja", fila) is TextBox caja)
                {
                    fila.BringIntoView();
                    caja.Focus();
                    Keyboard.Focus(caja);
                }
            }
        }

        private void AlTeclearCaja(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                return;
            }
            var caja = (TextBox)sender;
            if (caja.DataContext is SesionVM vm && vm.Enviar(caja.Text))
            {
                caja.Clear();
            }
            e.Handled = true;
        }

        private void AlCerrar(object sender, RoutedEventArgs e) => Hide();
        private void AlCambiarTira(object sender, RoutedEventArgs e) => CambiarTira?.Invoke(this, EventArgs.Empty);
        private void AlCambiarArranque(object sender, RoutedEventArgs e) => CambiarArranque?.Invoke(this, EventArgs.Empty);
        private void AlSalir(object sender, RoutedEventArgs e) => Salir?.Invoke(this, EventArgs.Empty);
        private void AlAbrirVentana(object sender, RoutedEventArgs e) => AbrirVentana?.Invoke(this, EventArgs.Empty);
    }
}
