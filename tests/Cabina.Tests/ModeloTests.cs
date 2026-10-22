using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cabina.Modelo;
using Xunit;

namespace Cabina.Tests
{
    public class ModeloTests
    {
        private const string Json = "{\"id\":\"s1\",\"cwd\":\"C:\\\\git\\\\harena\",\"proyecto\":\"harena\",\"origen\":\"Terminal\"," +
            "\"estado\":\"trabajando\",\"pedido\":\"sigue con los lados\",\"actividad\":\"Edita poses.py\",\"desde\":1761120000.5," +
            "\"ts\":1761120010,\"procesos\":[{\"pid\":10,\"exe\":\"bash.exe\"},{\"pid\":11,\"exe\":\"claude.exe\"}," +
            "{\"pid\":12,\"exe\":\"powershell.exe\"},{\"pid\":13,\"exe\":\"WindowsTerminal.exe\"}]}";

        private sealed class ProcesosFalsos : IConsultaProcesos
        {
            public Dictionary<int, ProcesoVivo> Vivos { get; } = new Dictionary<int, ProcesoVivo>();
            public Dictionary<int, List<VentanaInfo>> SusVentanas { get; } = new Dictionary<int, List<VentanaInfo>>();
            public Dictionary<int, string> Carpetas { get; } = new Dictionary<int, string>();
            public void Vivo(int pid, int padre, string exe) => Vivos[pid] = new ProcesoVivo(pid, padre, exe);
            public IReadOnlyDictionary<int, ProcesoVivo> Tabla() => Vivos;
            public IReadOnlyList<VentanaInfo> Ventanas(int pid) =>
                SusVentanas.TryGetValue(pid, out var v) ? v : new List<VentanaInfo>();
            public string? Carpeta(int pid) => Carpetas.TryGetValue(pid, out var c) ? c : null;
            public DateTime? Inicio(int pid) => new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
        }

        private static Sesion S(string estado, double desde, string id = "x") =>
            new Sesion { Id = id, EstadoTexto = estado, Desde = desde };

        [Fact]
        public void Lee_el_json_del_hook()
        {
            var s = Sesion.Leer(Json)!;
            Assert.Equal("harena", s.Proyecto);
            Assert.Equal(EstadoSesion.Trabajando, s.Estado);
            Assert.Equal("Edita poses.py", s.Actividad);
            Assert.Equal("", s.Aviso);
            Assert.Equal(4, s.Procesos.Count);
            Assert.Equal(new DateTime(2025, 10, 22, 8, 0, 0, 500, DateTimeKind.Utc), s.DesdeUtc);
        }

        [Fact]
        public void Json_roto_o_sin_id_no_es_sesion()
        {
            Assert.Null(Sesion.Leer("{\"id\":\"s1\""));
            Assert.Null(Sesion.Leer("{\"estado\":\"trabajando\"}"));
        }

        [Fact]
        public void Ordena_espera_trabajo_terminado_sin_ver_y_resto()
        {
            var vistas = new HashSet<string> { "visto" };
            var lista = new[] { S("inicio", 9, "a"), S("terminado", 8, "visto"), S("trabajando", 1, "t1"), S("terminado", 2, "nuevo"),
                S("esperando", 3, "e"), S("trabajando", 5, "t2") };
            var orden = Panorama.Ordenar(lista, s => vistas.Contains(s.Id)).Select(s => s.Id);
            Assert.Equal(new[] { "e", "t2", "t1", "nuevo", "visto", "a" }, orden);
        }

        [Fact]
        public void Lo_terminado_ya_visto_se_apaga_y_no_cuenta_en_la_bandeja()
        {
            Func<Sesion, bool> vista = s => s.Id == "visto";
            Assert.Equal(EstadoSesion.Inicio, Panorama.Visual(S("terminado", 1, "visto"), true));
            Assert.Equal(EstadoSesion.Terminado, Panorama.Visual(S("terminado", 1, "otro"), false));
            Assert.Equal(EstadoSesion.Trabajando, Panorama.Visual(S("trabajando", 1), true));
            Assert.Null(Panorama.Global(new[] { S("terminado", 1, "visto"), S("inicio", 1) }, vista));
            Assert.Equal(EstadoSesion.Esperando, Panorama.Global(new[] { S("trabajando", 1), S("esperando", 1) }, vista));
        }

        [Fact]
        public void Textos_de_tiempo()
        {
            Assert.Equal("45 s", Panorama.Duracion(TimeSpan.FromSeconds(45)));
            Assert.Equal("3:07", Panorama.Duracion(TimeSpan.FromSeconds(187)));
            Assert.Equal("1 h 05", Panorama.Duracion(TimeSpan.FromMinutes(65)));
            Assert.Equal("ahora", Panorama.Hace(TimeSpan.FromSeconds(30)));
            Assert.Equal("hace 4 min", Panorama.Hace(TimeSpan.FromMinutes(4.5)));
            Assert.Equal("hace 3 d", Panorama.Hace(TimeSpan.FromDays(3.2)));
        }

        [Fact]
        public void Sesion_viva_solo_si_su_claude_sigue_siendo_claude()
        {
            var s = Sesion.Leer(Json)!;
            var p = new ProcesosFalsos();
            Assert.False(Procesos.Viva(s, p.Tabla()));
            p.Vivo(11, 12, "notepad.exe");
            Assert.False(Procesos.Viva(s, p.Tabla()));
            p.Vivo(11, 12, "Claude.exe");
            Assert.True(Procesos.Viva(s, p.Tabla()));
        }

        [Fact]
        public void La_ventana_es_la_primera_subiendo_desde_claude()
        {
            var s = Sesion.Leer(Json)!;
            var p = new ProcesosFalsos();
            p.Vivo(10, 11, "bash.exe");
            p.SusVentanas[10] = new List<VentanaInfo> { new VentanaInfo((IntPtr)99, "bash") };
            p.Vivo(11, 12, "claude.exe");
            p.Vivo(12, 13, "powershell.exe");
            p.Vivo(13, 1, "WindowsTerminal.exe");
            p.SusVentanas[13] = new List<VentanaInfo> { new VentanaInfo((IntPtr)7, "PowerShell") };
            Assert.Equal((IntPtr)7, Procesos.VentanaDe(s, p));
        }

        [Fact]
        public void Con_varias_ventanas_elige_la_del_proyecto()
        {
            var s = Sesion.Leer(Json)!;
            var p = new ProcesosFalsos();
            p.Vivo(11, 13, "claude.exe");
            p.Vivo(13, 1, "WindowsTerminal.exe");
            p.SusVentanas[13] = new List<VentanaInfo>
            {
                new VentanaInfo((IntPtr)1, "poses.py - qubolab - Visual Studio Code"),
                new VentanaInfo((IntPtr)2, "poses.py - Harena - Visual Studio Code"),
            };
            Assert.Equal((IntPtr)2, Procesos.VentanaDe(s, p));
        }

        [Fact]
        public void Los_claude_sin_sesion_salen_sin_datos_y_claude_desktop_no()
        {
            var anotada = Sesion.Leer(Json)!;
            var p = new ProcesosFalsos();
            p.Vivo(11, 12, "claude.exe");
            p.Vivo(12, 13, "powershell.exe");
            p.Vivo(20, 21, "claude.exe");
            p.Vivo(21, 22, "Code.exe");
            p.Vivo(22, 1, "Code.exe");
            p.Carpetas[20] = @"C:\Users\ana\proyectos\hub";
            p.Vivo(30, 31, "claude.exe");
            p.Vivo(31, 1, "explorer.exe");
            p.Vivo(40, 20, "claude.exe");
            var nuevas = Procesos.SinDatos(new[] { anotada }, p, @"C:\Users\ana");
            var s = Assert.Single(nuevas);
            Assert.Equal(("proceso-20", "hub", "VS Code", true), (s.Id, s.Proyecto, s.Origen, s.SinDatos));
            Assert.Equal(new[] { 20, 21, 22 }, s.Procesos.Select(x => x.Pid));
            Assert.Equal("~", Procesos.NombreProyecto(@"C:\Users\ana\", @"C:\Users\ana"));
        }

        [Fact]
        public void El_almacen_borra_las_sesiones_muertas_pasada_la_gracia()
        {
            string carpeta = Path.Combine(Path.GetTempPath(), "cabina-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(carpeta);
            try
            {
                File.WriteAllText(Path.Combine(carpeta, "s1.json"), Json);
                File.WriteAllText(Path.Combine(carpeta, "roto.json"), "{");
                var p = new ProcesosFalsos();
                var almacen = new AlmacenSesiones(carpeta, p);
                var ts = new DateTime(2025, 10, 22, 8, 0, 10, DateTimeKind.Utc);
                Assert.Single(almacen.Cargar(ts.AddSeconds(5)));
                Assert.Empty(almacen.Cargar(ts.AddMinutes(1)));
                Assert.False(File.Exists(Path.Combine(carpeta, "s1.json")));
            }
            finally
            {
                Directory.Delete(carpeta, true);
            }
        }
    }
}
