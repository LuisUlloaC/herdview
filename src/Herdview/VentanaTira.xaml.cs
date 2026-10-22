using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Herdview
{
    /// <summary>La tira siempre encima: una ficha por sesión activa. Nunca se activa ni roba el foco (no sale en Alt+Tab
    /// ni en la barra de tareas); clic en una ficha trae la ventana de esa sesión; el asa la mueve o abre el panel.</summary>
    public partial class VentanaTira : Window
    {
        private const double Umbral = 4;

        private Point? _pulsado;
        private Point _origen;
        private bool _arrastrando;

        public VentanaTira(ObservableCollection<SesionVM> sesiones)
        {
            InitializeComponent();
            Fichas.ItemsSource = sesiones;
            SourceInitialized += (_, __) => Win32.HacerNoActivable(new WindowInteropHelper(this).Handle);
            Asa.MouseLeftButtonDown += AlPulsarAsa;
            Asa.MouseMove += AlMoverAsa;
            Asa.MouseLeftButtonUp += AlSoltarAsa;
        }

        public void PonerVacia(bool vacia) => Vacia.Visibility = vacia ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Clic en el asa sin arrastrar.</summary>
        public event EventHandler? AsaPulsada;

        /// <summary>La tira acabó de moverse a (Left, Top).</summary>
        public event EventHandler? Movida;

        /// <summary>Sin posición guardada: arriba, centrada en la pantalla principal.</summary>
        public void Colocar(double? x, double? y)
        {
            UpdateLayout();
            Rect area = SystemParameters.WorkArea;
            double ancho = ActualWidth > 0 ? ActualWidth : 200;
            double alto = ActualHeight > 0 ? ActualHeight : 30;
            double left = x ?? area.Left + (area.Width - ancho) / 2;
            double top = y ?? area.Top + 6;
            Rect virtual_ = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            Left = Math.Max(virtual_.Left, Math.Min(left, virtual_.Right - Math.Min(ancho, 60)));
            Top = Math.Max(virtual_.Top, Math.Min(top, virtual_.Bottom - alto));
        }

        private Point Cursor_()
        {
            Win32.GetCursorPos(out var p);
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
        }

        private void AlPulsarAsa(object sender, MouseButtonEventArgs e)
        {
            _pulsado = Cursor_();
            _origen = new Point(Left, Top);
            _arrastrando = false;
            Asa.CaptureMouse();
            e.Handled = true;
        }

        private void AlMoverAsa(object sender, MouseEventArgs e)
        {
            if (_pulsado == null)
            {
                return;
            }
            Vector d = Cursor_() - _pulsado.Value;
            if (!_arrastrando && d.Length < Umbral)
            {
                return;
            }
            _arrastrando = true;
            Left = _origen.X + d.X;
            Top = _origen.Y + d.Y;
        }

        private void AlSoltarAsa(object sender, MouseButtonEventArgs e)
        {
            if (_pulsado == null)
            {
                return;
            }
            _pulsado = null;
            Asa.ReleaseMouseCapture();
            if (_arrastrando)
            {
                Movida?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                AsaPulsada?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
    }
}
