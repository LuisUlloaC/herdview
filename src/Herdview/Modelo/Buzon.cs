using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Herdview.Modelo
{
    /// <summary>Lo que el usuario escribe a una sesión desde Herdview: `buzon\&lt;sesión&gt;.txt`. El hook de la sesión
    /// (herdview.py esperar, en Stop con asyncRewake) lo recoge y despierta a Claude con él.</summary>
    public static class Buzon
    {
        public static string CarpetaPorDefecto =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Herdview", "buzon");

        /// <summary>El mismo nombre de fichero que usa el hook (solo letras, cifras, «-» y «_»).</summary>
        public static string RutaDe(string carpeta, string sesionId)
        {
            string seguro = new string(sesionId.Where(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '-' || c == '_').ToArray());
            return Path.Combine(carpeta, (seguro.Length > 0 ? seguro : "sin-id") + ".txt");
        }

        /// <summary>Deja el mensaje (se suma a los que aún no haya recogido). Se escribe aparte y se cambia de golpe:
        /// el hook nunca lee un mensaje a medias.</summary>
        public static void Enviar(string carpeta, string sesionId, string texto)
        {
            Directory.CreateDirectory(carpeta);
            string ruta = RutaDe(carpeta, sesionId);
            string previo = "";
            try
            {
                previo = File.ReadAllText(ruta, Encoding.UTF8).Trim();
            }
            catch (FileNotFoundException)
            {
            }
            string todo = previo.Length > 0 ? previo + "\n\n" + texto.Trim() : texto.Trim();
            string temporal = ruta + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporal, todo, new UTF8Encoding(false));
            try
            {
                if (File.Exists(ruta))
                {
                    File.Replace(temporal, ruta, null);
                    return;
                }
            }
            catch (FileNotFoundException)
            {
                // El hook lo recogió justo ahora: va como mensaje nuevo.
            }
            File.Move(temporal, ruta);
        }
    }
}
