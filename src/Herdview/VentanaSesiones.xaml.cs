using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Herdview.Modelo;

namespace Herdview
{
    /// <summary>La vista en ventana: todas las sesiones como filas de un tablero, con lo que hace cada una y su consumo
    /// de tokens. Cerrarla solo la esconde (Herdview sigue en la bandeja).</summary>
    public partial class VentanaSesiones : Window
    {
        private bool _cerrando;

        public VentanaSesiones(ObservableCollection<SesionVM> sesiones)
        {
            InitializeComponent();
            Lista.ItemsSource = sesiones;
            SourceInitialized += (_, __) => Win32.BarraOscura(new WindowInteropHelper(this).Handle);
        }

        public event EventHandler? NuevaSesion;

        private void AlNuevaSesion(object sender, RoutedEventArgs e) => NuevaSesion?.Invoke(this, EventArgs.Empty);

        /// <summary>Al salir de la app sí se cierra de verdad.</summary>
        public void CerrarDeVerdad()
        {
            _cerrando = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_cerrando)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        public void Pintar(string resumen, IReadOnlyCollection<SesionVM> sesiones)
        {
            Resumen.Text = resumen;
            Vacio.Visibility = sesiones.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            var medidas = sesiones.Select(s => s.Consumo).Where(c => c != null).Cast<Consumo>().ToList();
            TotalEntrada.Text = medidas.Count == 0 ? "—" : Panorama.Tokens(medidas.Sum(c => c.EntradaNueva));
            TotalCache.Text = medidas.Count == 0 ? "—" : Panorama.Tokens(medidas.Sum(c => c.CacheLeida));
            TotalSalida.Text = medidas.Count == 0 ? "—" : Panorama.Tokens(medidas.Sum(c => c.Salida));
        }

        public void Abrir()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
        }
    }
}
