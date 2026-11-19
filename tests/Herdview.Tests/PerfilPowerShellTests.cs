using System;
using System.IO;
using System.Linq;
using System.Text;
using Herdview.Modelo;
using Xunit;

namespace Herdview.Tests
{
    public class PerfilPowerShellTests : IDisposable
    {
        private readonly string _documentos = Path.Combine(Path.GetTempPath(), "herdview-perfil-" + Guid.NewGuid().ToString("N"));

        public PerfilPowerShellTests() => Directory.CreateDirectory(_documentos);

        public void Dispose()
        {
            if (Directory.Exists(_documentos)) Directory.Delete(_documentos, true);
        }

        private string Perfil => Path.Combine(_documentos, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1");

        private const string Hecho_a_mano =
            "\r\n# Claude Code - cuenta alternativa (config aislada)\r\n" +
            "function claude-alt { $env:CLAUDE_CONFIG_DIR = \"$HOME\\.claude-alt\"; claude @args }\r\n";

        [Fact]
        public void La_funcion_es_la_misma_que_se_escribe_a_mano()
        {
            Assert.Equal(
                "# Claude Code - cuenta testing (config aislada)\r\n" +
                "function claude-testing { $env:CLAUDE_CONFIG_DIR = \"$HOME\\.claude-testing\"; claude @args }\r\n",
                PerfilPowerShell.Bloque("testing"));
        }

        [Fact]
        public void Va_al_perfil_de_Windows_PowerShell_y_al_de_PowerShell_7_solo_si_esta_su_carpeta()
        {
            Assert.Equal(new[] { Perfil }, PerfilPowerShell.Rutas(_documentos));
            Directory.CreateDirectory(Path.Combine(_documentos, "PowerShell"));
            Assert.Equal(Path.Combine(_documentos, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
                PerfilPowerShell.Rutas(_documentos).Last());
        }

        [Fact]
        public void Sin_perfil_lo_crea_en_UTF8_con_BOM()
        {
            Assert.True(PerfilPowerShell.Anadir(Perfil, "testing"));
            byte[] bytes = File.ReadAllBytes(Perfil);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
            Assert.Equal(PerfilPowerShell.Bloque("testing"), Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }

        [Fact]
        public void Anade_al_final_sin_tocar_lo_que_habia_ni_repetir_el_BOM()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Perfil)!);
            File.WriteAllText(Perfil, Hecho_a_mano, new UTF8Encoding(true));
            byte[] antes = File.ReadAllBytes(Perfil);

            Assert.True(PerfilPowerShell.Anadir(Perfil, "testing"));

            byte[] despues = File.ReadAllBytes(Perfil);
            Assert.Equal(antes, despues.Take(antes.Length));
            Assert.Equal("\r\n" + PerfilPowerShell.Bloque("testing"),
                Encoding.UTF8.GetString(despues, antes.Length, despues.Length - antes.Length));
        }

        [Fact]
        public void Si_el_perfil_no_acaba_en_salto_de_linea_lo_pone_antes()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Perfil)!);
            File.WriteAllText(Perfil, "Set-Location C:\\git", new UTF8Encoding(false));
            PerfilPowerShell.Anadir(Perfil, "testing");
            Assert.Equal("Set-Location C:\\git\r\n\r\n" + PerfilPowerShell.Bloque("testing"), File.ReadAllText(Perfil));
        }

        [Fact]
        public void Respeta_un_perfil_en_UTF16_como_los_que_deja_Out_File()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Perfil)!);
            File.WriteAllText(Perfil, Hecho_a_mano, new UnicodeEncoding(false, true));
            PerfilPowerShell.Anadir(Perfil, "testing");
            byte[] bytes = File.ReadAllBytes(Perfil);
            Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes.Take(2));
            Assert.Equal(Hecho_a_mano + "\r\n" + PerfilPowerShell.Bloque("testing"),
                Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }

        [Fact]
        public void No_repite_un_comando_que_ya_existe_aunque_sea_a_mano()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Perfil)!);
            File.WriteAllText(Perfil, Hecho_a_mano + "  Set-Alias claude-juegos claude\r\n", new UTF8Encoding(true));
            string antes = File.ReadAllText(Perfil);
            Assert.False(PerfilPowerShell.Anadir(Perfil, "alt"));
            Assert.False(PerfilPowerShell.Anadir(Perfil, "juegos"));
            Assert.Equal(antes, File.ReadAllText(Perfil));
            Assert.True(PerfilPowerShell.Anadir(Perfil, "testing"));
            Assert.False(PerfilPowerShell.Anadir(Perfil, "testing"));
        }

        [Fact]
        public void Un_nombre_que_empieza_como_otro_no_cuenta_como_definido()
        {
            Assert.False(PerfilPowerShell.Define(Hecho_a_mano, "al"));
            Assert.False(PerfilPowerShell.Define("function claude-alt-2 { }", "alt"));
            Assert.False(PerfilPowerShell.Define("# function claude-alt { }", "alt"));
        }
    }
}
