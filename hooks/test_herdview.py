"""python -I -m unittest discover -s hooks"""
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import io
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import herdview


def evento(nombre, **extra):
    return dict(session_id="s1", cwd=r"C:\git\harena", hook_event_name=nombre, **extra)


class Aplicar(unittest.TestCase):
    def test_pedido_pone_trabajando_y_guarda_el_pedido(self):
        s = herdview.aplicar({}, evento("UserPromptSubmit", prompt="  sigue con\nlos lados "), 10)
        self.assertEqual(s["estado"], "trabajando")
        self.assertEqual(s["pedido"], "sigue con los lados")
        self.assertEqual(s["proyecto"], "harena")
        self.assertEqual(s["desde"], 10)

    def test_herramienta_no_reinicia_el_cronometro(self):
        s = herdview.aplicar({}, evento("UserPromptSubmit", prompt="x"), 10)
        s = herdview.aplicar(s, evento("PreToolUse", tool_name="Bash", tool_input={"command": "ls", "description": "Lista"}), 20)
        self.assertEqual((s["estado"], s["desde"], s["actividad"]), ("trabajando", 10, "Lista"))

    def test_pregunta_espera_y_vuelve_a_trabajar(self):
        s = herdview.aplicar({}, evento("PreToolUse", tool_name="AskUserQuestion", tool_input={}), 10)
        self.assertEqual(s["estado"], "esperando")
        s = herdview.aplicar(s, evento("PostToolUse", tool_name="AskUserQuestion"), 30)
        self.assertEqual((s["estado"], s["desde"], s["aviso"]), ("trabajando", 30, ""))

    def test_permiso_espera_pero_el_aviso_de_inactividad_no(self):
        s = herdview.aplicar({"estado": "terminado", "desde": 5}, evento("Notification", message="Claude is waiting for your input"), 9)
        self.assertEqual((s["estado"], s["desde"]), ("terminado", 5))
        s = herdview.aplicar(s, evento("Notification", message="Claude needs your permission to use Bash"), 12)
        self.assertEqual((s["estado"], s["aviso"]), ("esperando", "Pide permiso para usar Bash"))

    def test_stop_termina_y_session_end_borra(self):
        s = herdview.aplicar({"estado": "trabajando", "actividad": "x"}, evento("Stop"), 40)
        self.assertEqual((s["estado"], s["actividad"]), ("terminado", ""))
        self.assertIsNone(herdview.aplicar(s, evento("SessionEnd"), 50))

    def test_el_proyecto_es_la_carpeta_donde_arranco(self):
        s = herdview.aplicar({}, evento("UserPromptSubmit", prompt="x"), 10)
        s = herdview.aplicar(s, dict(evento("PreToolUse", tool_name="Bash", tool_input={}), cwd=r"C:\otra"), 11)
        self.assertEqual(s["proyecto"], "harena")
        self.assertEqual(herdview.nombre_proyecto(os.path.expanduser("~")), "~")

    def test_compactar_no_cambia_el_estado(self):
        s = herdview.aplicar({"estado": "trabajando", "desde": 3}, evento("SessionStart", source="compact"), 40)
        self.assertEqual((s["estado"], s["desde"]), ("trabajando", 3))


class Actividad(unittest.TestCase):
    def test_textos(self):
        self.assertEqual(herdview.actividad("Edit", {"file_path": r"C:\git\a\poses.py"}), "Edita poses.py")
        self.assertEqual(herdview.actividad("Grep", {"pattern": "TODO"}), "Busca «TODO»")
        self.assertEqual(herdview.actividad("Agent", {"description": "Revisar"}), "Agente: Revisar")
        self.assertEqual(herdview.actividad("mcp__claude_ai_Docs__read", {}), "read")
        self.assertTrue(herdview.actividad("Bash", {"command": "x" * 500}).endswith("…"))


class Buzon(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.antes = (herdview.CARPETA, herdview.BUZON)
        herdview.CARPETA = os.path.join(self.tmp.name, "sesiones")
        herdview.BUZON = os.path.join(self.tmp.name, "buzon")

    def tearDown(self):
        herdview.CARPETA, herdview.BUZON = self.antes
        self.tmp.cleanup()

    @staticmethod
    def luego(segundos, accion):
        hilo = threading.Timer(segundos, accion)
        hilo.start()
        return hilo

    @staticmethod
    def escribir(ruta, texto):
        os.makedirs(os.path.dirname(ruta), exist_ok=True)
        with open(ruta, "w", encoding="utf-8") as fh:
            fh.write(texto)

    def test_el_mensaje_despierta_con_2_y_queda_como_pedido(self):
        self.luego(0.15, lambda: self.escribir(herdview.buzon_de("s1", ".txt"), "  sigue con los lados "))
        error = io.StringIO()
        antes, sys.stderr = sys.stderr, error
        try:
            codigo = herdview.esperar(evento("Stop"), time.time() + 5, pausa=0.02)
        finally:
            sys.stderr = antes
        self.assertEqual(codigo, 2)
        self.assertTrue(error.getvalue().endswith("sigue con los lados"))
        self.assertFalse(os.path.exists(herdview.buzon_de("s1", ".txt")))
        self.assertFalse(os.path.exists(herdview.buzon_de("s1", ".espera")))
        with open(herdview.ruta_de("s1"), encoding="utf-8") as fh:
            s = json.load(fh)
        self.assertEqual((s["estado"], s["pedido"]), ("trabajando", "sigue con los lados"))

    def test_otra_espera_la_releva(self):
        self.luego(0.15, lambda: self.escribir(herdview.buzon_de("s1", ".espera"), "otra"))
        self.assertEqual(herdview.esperar(evento("Stop"), time.time() + 5, pausa=0.02), 0)

    def test_escribir_en_la_ventana_la_cancela(self):
        self.luego(0.15, lambda: herdview.registrar(evento("UserPromptSubmit", prompt="x"), time.time()))
        self.assertEqual(herdview.esperar(evento("Stop"), time.time() + 5, pausa=0.02), 0)

    def test_sin_mensajes_acaba_al_limite(self):
        self.assertEqual(herdview.esperar(evento("Stop"), time.time() + 0.1, pausa=0.02), 0)


class Proceso(unittest.TestCase):
    def test_escribe_y_borra_el_fichero(self):
        with tempfile.TemporaryDirectory() as tmp:
            entorno = dict(os.environ, LOCALAPPDATA=tmp)
            correr = lambda e: subprocess.run([sys.executable, "-I", herdview.__file__], input=json.dumps(e).encode(),
                                              env=entorno, capture_output=True, check=True)
            correr(evento("UserPromptSubmit", prompt="hola"))
            ruta = os.path.join(tmp, "Herdview", "sesiones", "s1.json")
            with open(ruta, encoding="utf-8") as fh:
                datos = json.load(fh)
            self.assertEqual(datos["estado"], "trabajando")
            self.assertTrue(any(p["exe"].lower() == "python.exe" for p in datos["procesos"]))
            correr(evento("SessionEnd"))
            self.assertFalse(os.path.exists(ruta))


if __name__ == "__main__":
    unittest.main()
