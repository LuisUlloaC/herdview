using System;
using System.IO;
using System.Linq;
using Herdview.Modelo;
using Xunit;

namespace Herdview.Tests
{
    public class AltaCuentaTests : IDisposable
    {
        private readonly string _usuario = Path.Combine(Path.GetTempPath(), "herdview-alta-" + Guid.NewGuid().ToString("N"));

        public AltaCuentaTests() => Directory.CreateDirectory(_usuario);

        public void Dispose()
        {
            if (Directory.Exists(_usuario)) Directory.Delete(_usuario, true);
        }

        private string Escribir(string relativa, string texto)
        {
            string ruta = Path.Combine(_usuario, relativa);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            File.WriteAllText(ruta, texto);
            return ruta;
        }

        [Fact]
        public void El_nombre_solo_admite_minusculas_numeros_y_guiones_y_no_repite_cuentas()
        {
            Directory.CreateDirectory(Path.Combine(_usuario, ".claude-alt"));
            Assert.Null(AltaCuenta.ErrorNombre(_usuario, "juegos-2"));
            Assert.NotNull(AltaCuenta.ErrorNombre(_usuario, ""));
            Assert.NotNull(AltaCuenta.ErrorNombre(_usuario, "Juegos"));
            Assert.NotNull(AltaCuenta.ErrorNombre(_usuario, "mis juegos"));
            Assert.NotNull(AltaCuenta.ErrorNombre(_usuario, "-raro"));
            Assert.NotNull(AltaCuenta.ErrorNombre(_usuario, "../fuera"));
            Assert.NotNull(AltaCuenta.ErrorNombre(_usuario, "normal"));
            Assert.Contains(".claude-alt", AltaCuenta.ErrorNombre(_usuario, "alt"));
            Assert.Null(AltaCuenta.ErrorNombre(null, "alt"));
        }

        [Fact]
        public void Copia_la_configuracion_pero_nunca_credenciales_identidad_ni_historial()
        {
            string origen = Path.Combine(_usuario, ".claude-alt");
            Escribir(@".claude-alt\settings.json", "{\"model\":\"opus\"}");
            Escribir(@".claude-alt\CLAUDE.md", "# memoria");
            Escribir(@".claude-alt\agents\editor.md", "agente");
            Escribir(@".claude-alt\skills\diseno\SKILL.md", "skill");
            Escribir(@".claude-alt\plugins\installed_plugins.json",
                "{\"installPath\": \"" + Path.Combine(origen, "plugins", "cache", "p").Replace(@"\", @"\\") + "\"}");
            Escribir(@".claude-alt\.credentials.json", "secreto");
            Escribir(@".claude-alt\.claude.json", "{\"oauthAccount\":{}}");
            Escribir(@".claude-alt\history.jsonl", "{}");
            Escribir(@".claude-alt\projects\p\s.jsonl", "{}");

            var nueva = AltaCuenta.Crear(_usuario, "juegos", Cuenta.De("alt", origen));

            string destino = Path.Combine(_usuario, ".claude-juegos");
            Assert.Equal(destino, nueva.Carpeta);
            Assert.True(nueva.SinSesion);
            Assert.Equal("{\"model\":\"opus\"}", File.ReadAllText(Path.Combine(destino, "settings.json")));
            Assert.True(File.Exists(Path.Combine(destino, "CLAUDE.md")));
            Assert.True(File.Exists(Path.Combine(destino, "agents", "editor.md")));
            Assert.True(File.Exists(Path.Combine(destino, "skills", "diseno", "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(destino, ".credentials.json")));
            Assert.False(File.Exists(Path.Combine(destino, ".claude.json")));
            Assert.False(File.Exists(Path.Combine(destino, "history.jsonl")));
            Assert.False(Directory.Exists(Path.Combine(destino, "projects")));
            string plugins = File.ReadAllText(Path.Combine(destino, "plugins", "installed_plugins.json"));
            Assert.Contains(Path.Combine(destino, "plugins", "cache", "p").Replace(@"\", @"\\"), plugins);
            Assert.DoesNotContain(".claude-alt", plugins);
            Assert.Contains(".claude-alt", File.ReadAllText(Path.Combine(origen, "plugins", "installed_plugins.json"))); // origen intacto
        }

        [Fact]
        public void Sin_copiar_queda_vacia_pero_con_settings_para_salir_en_la_lista_y_sin_sesion()
        {
            AltaCuenta.Crear(_usuario, "nueva", null);
            Assert.Equal("{}", File.ReadAllText(Path.Combine(_usuario, ".claude-nueva", "settings.json")).Trim());
            var cuenta = Lanzador.Cuentas(_usuario).Single();
            Assert.Equal("nueva", cuenta.Nombre);
            Assert.True(cuenta.SinSesion);
            Assert.Throws<InvalidOperationException>(() => AltaCuenta.Crear(_usuario, "nueva", null));
        }

        [Fact]
        public void Encuentra_el_hook_que_ya_usan_otras_cuentas_con_barras_o_escapado()
        {
            string hook = Escribir(@"hooks\herdview.py", "# hook");
            var sinHook = Cuenta.De("vacia", Path.GetDirectoryName(Escribir(@".claude-vacia\settings.json", "{}"))!);
            var conBarras = Cuenta.De("alt", Path.GetDirectoryName(Escribir(@".claude-alt\settings.json",
                "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"command\":\"python -I \\\"" + hook.Replace('\\', '/') + "\\\" esperar\"}]}]}}"))!);
            var escapado = Cuenta.De("otra", Path.GetDirectoryName(Escribir(@".claude-otra\settings.json",
                "{\"command\":\"python -I \\\"" + hook.Replace(@"\", @"\\") + "\\\"\"}"))!);

            Assert.False(AltaCuenta.TieneHook(sinHook.Carpeta));
            Assert.True(AltaCuenta.TieneHook(conBarras.Carpeta));
            Assert.Null(AltaCuenta.HookConocido(new[] { sinHook }));
            Assert.Equal(hook.Replace('\\', '/'), AltaCuenta.HookConocido(new[] { sinHook, conBarras }));
            Assert.Equal(hook, AltaCuenta.HookConocido(new[] { escapado }));
            File.Delete(hook);
            Assert.Null(AltaCuenta.HookConocido(new[] { conBarras, escapado }));
        }
    }
}
