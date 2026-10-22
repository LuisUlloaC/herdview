using System;
using System.IO;
using System.Linq;
using System.Text;
using Herdview.Modelo;
using Xunit;

namespace Herdview.Tests
{
    public class LanzadorTests : IDisposable
    {
        private readonly string _usuario = Path.Combine(Path.GetTempPath(), "herdview-usuario-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_usuario)) Directory.Delete(_usuario, true);
        }

        [Fact]
        public void Detecta_las_cuentas_con_configuracion_y_la_normal_al_final()
        {
            foreach (var d in new[] { ".claude", ".claude-alt", ".claude-trabajo", ".claude-vacia" })
            {
                Directory.CreateDirectory(Path.Combine(_usuario, d));
            }
            File.WriteAllText(Path.Combine(_usuario, ".claude-alt", "settings.json"), "{}");
            Directory.CreateDirectory(Path.Combine(_usuario, ".claude-trabajo", "projects"));
            var cuentas = Lanzador.Cuentas(_usuario);
            Assert.Equal(new[] { "alt", "trabajo", "normal" }, cuentas.Select(c => c.Nombre));
            Assert.True(cuentas[2].EsNormal);
        }

        [Fact]
        public void El_script_pone_la_cuenta_y_el_pedido_con_comillas_escapadas()
        {
            var alt = new Cuenta("alt", @"C:\Users\ana\.claude-alt");
            Assert.Equal("$env:CLAUDE_CONFIG_DIR = 'C:\\Users\\ana\\.claude-alt'\r\nclaude 'sigue con lo de ''ayer''; y avisa'",
                Lanzador.Script(alt, "  sigue con lo de 'ayer'; y avisa "));
            Assert.StartsWith("Remove-Item Env:CLAUDE_CONFIG_DIR", Lanzador.Script(new Cuenta("normal", @"C:\x\.claude"), ""));
            Assert.EndsWith("\r\nclaude", Lanzador.Script(alt, "  "));
        }

        [Fact]
        public void Los_argumentos_de_wt_llevan_carpeta_titulo_y_el_script_codificado_sin_punto_y_coma()
        {
            var alt = new Cuenta("alt", @"C:\Users\ana\.claude-alt");
            string args = Lanzador.ArgumentosTerminal(@"C:\git\mi proyecto\", alt, "hola; adiós");
            Assert.StartsWith("-w 0 new-tab --title \"mi proyecto\" -d \"C:\\git\\mi proyecto\" powershell.exe -NoLogo -NoExit -EncodedCommand ", args);
            Assert.DoesNotContain(";", args);
            string b64 = args.Split(' ').Last();
            Assert.Contains("claude 'hola; adiós'", Encoding.Unicode.GetString(Convert.FromBase64String(b64)));
        }

        [Fact]
        public void Recientes_lo_usado_va_primero_y_lo_visto_al_final()
        {
            var r = new Recientes(new[] { @"C:\a", @"C:\b" });
            Assert.True(r.Usar(@"C:\b\"));
            Assert.Equal(new[] { @"C:\b", @"C:\a" }, r.Carpetas);
            Assert.False(r.Usar(@"C:\B"));
            Assert.True(r.Conocer(@"C:\c"));
            Assert.False(r.Conocer(@"C:\A"));
            Assert.Equal(new[] { @"C:\b", @"C:\a", @"C:\c" }, r.Carpetas);
        }
    }
}
