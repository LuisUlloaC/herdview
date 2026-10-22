using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Herdview.Modelo;
using Xunit;

namespace Herdview.Tests
{
    public class DockerTests
    {
        /// <summary>Docker falso: responde según el script y apunta cada llamada.</summary>
        private sealed class DockerFalso : IDocker
        {
            public List<Contenedor> Contenedores { get; } = new List<Contenedor>();
            public List<(string Contenedor, string Script, string? Entrada, string[] Args)> Llamadas { get; } =
                new List<(string, string, string?, string[])>();
            public Func<string, string, (int, string)> Responder { get; set; } = (_, __) => (0, "");

            public IReadOnlyList<Contenedor> EnMarcha() => Contenedores;

            public (int Codigo, string Salida) Sh(string contenedor, string script, string? entrada = null, params string[] args)
            {
                Llamadas.Add((contenedor, script, entrada, args));
                return Responder(contenedor, script);
            }
        }

        [Fact]
        public void Lee_las_sesiones_de_los_contenedores_preparados()
        {
            var d = new DockerFalso();
            d.Contenedores.Add(new Contenedor("abc", "api", "node:22"));
            d.Contenedores.Add(new Contenedor("def", "sin-hook", "alpine"));
            d.Responder = (c, _) => c == "abc"
                ? (0, "{\"id\":\"s1\",\"proyecto\":\"app\",\"estado\":\"trabajando\",\"consumo\":{\"entrada\":5,\"salida\":7,\"contexto\":90,\"modelo\":\"claude-opus-5-5\"}}\n\n")
                : (1, "");
            var s = Assert.Single(EnDocker.Sesiones(d));
            Assert.Equal(("s1", "api", "abc", "Docker · api"), (s.Id, s.Contenedor, s.ContenedorId, s.Origen));
            var c = s.ConsumoHook!.AConsumo();
            Assert.Equal((5L, 7L, 90L, "claude-opus-5-5"), (c.Entrada, c.Salida, c.Contexto, c.Modelo));
        }

        [Fact]
        public void Enviar_pasa_el_texto_por_stdin_y_la_sesion_como_argumento()
        {
            var d = new DockerFalso();
            Assert.True(EnDocker.Enviar(d, new Sesion { Id = "s1", ContenedorId = "abc" }, "hola; 'adiós'"));
            var llamada = Assert.Single(d.Llamadas);
            Assert.Equal(("abc", "hola; 'adiós'"), (llamada.Contenedor, llamada.Entrada));
            Assert.Equal(new[] { "s1" }, llamada.Args);
            Assert.Contains("enviar \"$1\"", llamada.Script);
        }

        [Fact]
        public void Cerrar_manda_term_y_luego_kill_al_pid_de_la_sesion()
        {
            var d = new DockerFalso();
            Assert.False(EnDocker.Cerrar(d, new Sesion { Id = "s1", ContenedorId = "abc" }));
            Assert.Empty(d.Llamadas);
            Assert.True(EnDocker.Cerrar(d, new Sesion { Id = "s1", ContenedorId = "abc", PidClaude = 63 }));
            var llamada = Assert.Single(d.Llamadas);
            Assert.Equal("abc", llamada.Contenedor);
            Assert.Equal(new[] { "63" }, llamada.Args);
            Assert.StartsWith("kill -TERM \"$1\"", llamada.Script);
            Assert.EndsWith("kill -KILL \"$1\"", llamada.Script);
        }

        [Fact]
        public void Preparar_avisa_si_no_hay_python_y_si_lo_hay_copia_e_instala()
        {
            var sinPython = new DockerFalso { Responder = (_, s) => s.StartsWith("command -v") ? (1, "") : (0, "") };
            Assert.Equal(EnDocker.Preparacion.SinPython, EnDocker.Preparar(sinPython, "abc", "# hook").Resultado);
            Assert.Single(sinPython.Llamadas);

            var d = new DockerFalso { Responder = (_, s) => (0, s.Contains("instalar") ? "/root/.claude/settings.json\n" : "") };
            var r = EnDocker.Preparar(d, "abc", "# hook");
            Assert.Equal((EnDocker.Preparacion.Lista, "/root/.claude/settings.json"), (r.Resultado, r.Detalle));
            Assert.Equal("# hook", d.Llamadas[1].Entrada);
            Assert.Contains("instalar", d.Llamadas[2].Script);
        }

        [Fact]
        public void El_script_de_apertura_lleva_el_pedido_en_base64()
        {
            string s = EnDocker.Script("api", "/work/app", "arregla \"esto\"; y 'aquello' $HOME");
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("arregla \"esto\"; y 'aquello' $HOME"));
            Assert.Equal("docker --% exec -it -e HERDVIEW_PEDIDO_B64=" + b64 + " -w \"/work/app\" api sh -lc \"exec claude \\\"$(echo $HERDVIEW_PEDIDO_B64 | base64 -d)\\\"\"", s);
            Assert.Equal("docker --% exec -it api sh -lc \"exec claude\"", EnDocker.Script("api", "", "  "));
            Assert.DoesNotContain(";", EnDocker.ArgumentosTerminal("api", "", "a;b"));
        }

        [Theory]
        [InlineData("ps", "ps")]
        [InlineData("con espacio", "\"con espacio\"")]
        [InlineData("f=\"$HOME/x\"; [ -f \"$f\" ]", "\"f=\\\"$HOME/x\\\"; [ -f \\\"$f\\\" ]\"")]
        [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
        [InlineData("", "\"\"")]
        public void Escapa_los_argumentos_como_Windows(string entrada, string esperado) =>
            Assert.Equal(esperado, DockerCli.Argumento(entrada));
    }
}
