"""Hook de Claude Code para Herdview: anota en un JSON por sesión qué está haciendo cada instancia de Claude.

Se registra (asíncrono) en SessionStart, UserPromptSubmit, PreToolUse, PostToolUse, Notification, Stop y SessionEnd:

    python -I <ruta>/herdview.py            (y en Stop, además: python -I <ruta>/herdview.py esperar)

Lee el evento de stdin y escribe %LOCALAPPDATA%\\Herdview\\sesiones\\<session_id>.json, que la app lee. Un fallo aquí
nunca debe molestar a la sesión: siempre sale con 0.
"""
import ctypes
import ctypes.wintypes as w
import json
import os
import re
import sys
import time

CARPETA = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "Herdview", "sesiones")
## Lo que el usuario escribe desde Herdview: <sesión>.txt; <sesión>.espera marca la espera activa (una por sesión).
BUZON = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "Herdview", "buzon")
## Algo menos que el timeout del hook en settings (43200 s): al llegar, la espera acaba sola sin error.
DURACION_ESPERA = 43000
LARGO_PEDIDO = 240
LARGO_ACTIVIDAD = 120

## Herramientas que dejan a la sesión esperando al usuario aunque no salte la notificación de permiso.
PREGUNTAS = {"AskUserQuestion": "Te hace una pregunta", "ExitPlanMode": "Espera que apruebes el plan"}


def recortar(texto: str, largo: int) -> str:
    texto = " ".join((texto or "").split())
    return texto if len(texto) <= largo else texto[: largo - 1] + "…"


def actividad(herramienta: str, entrada: dict) -> str:
    """Una línea legible de lo que hace la herramienta, como se vería en la consola."""
    e = entrada or {}
    ruta = e.get("file_path") or e.get("notebook_path") or e.get("path") or ""
    nombre = os.path.basename(ruta.rstrip("/\\")) if ruta else ""
    if herramienta in ("Bash", "PowerShell"):
        texto = e.get("description") or e.get("command") or herramienta
    elif herramienta in ("Read", "Edit", "Write", "NotebookEdit"):
        texto = {"Read": "Lee", "Edit": "Edita", "Write": "Escribe", "NotebookEdit": "Edita"}[herramienta] + " " + (nombre or "un fichero")
    elif herramienta in ("Grep", "Glob"):
        texto = "Busca «%s»" % (e.get("pattern") or "")
    elif herramienta == "Agent":
        texto = "Agente: " + (e.get("description") or e.get("subagent_type") or "")
    elif herramienta in ("WebSearch", "WebFetch"):
        texto = "Web: " + (e.get("query") or e.get("url") or "")
    elif herramienta == "Skill":
        texto = "Skill " + (e.get("skill") or "")
    elif herramienta.startswith("mcp__"):
        texto = herramienta.split("__")[-1].replace("_", " ")
    else:
        texto = herramienta
    return recortar(texto, LARGO_ACTIVIDAD)


def traducir(mensaje: str) -> str:
    """Los avisos de Claude Code llegan en inglés."""
    m = re.match(r"Claude needs your permission to use (.+)", mensaje or "")
    if m:
        return "Pide permiso para usar " + m.group(1)
    return mensaje


def ancestros(limite: int = 14) -> list:
    """[{pid, exe}] desde el padre de este proceso hacia arriba (el hook cuelga de claude.exe por un shell)."""
    class Entrada(ctypes.Structure):
        _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("th32ProcessID", w.DWORD),
                    ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", w.DWORD), ("cntThreads", w.DWORD),
                    ("th32ParentProcessID", w.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", w.DWORD),
                    ("szExeFile", ctypes.c_wchar * 260)]
    k = ctypes.windll.kernel32
    k.CreateToolhelp32Snapshot.restype = w.HANDLE
    foto = k.CreateToolhelp32Snapshot(2, 0)
    e = Entrada()
    e.dwSize = ctypes.sizeof(Entrada)
    tabla = {}
    ok = k.Process32FirstW(foto, ctypes.byref(e))
    while ok:
        tabla[e.th32ProcessID] = (e.th32ParentProcessID, e.szExeFile)
        ok = k.Process32NextW(foto, ctypes.byref(e))
    k.CloseHandle(foto)
    cadena, pid = [], tabla.get(os.getpid(), (0, ""))[0]
    while pid in tabla and len(cadena) < limite:
        padre, exe = tabla[pid]
        cadena.append({"pid": pid, "exe": exe})
        if padre == pid:
            break
        pid = padre
    return cadena


def origen(cadena: list) -> str:
    nombres = [p["exe"].lower() for p in cadena]
    if "code.exe" in nombres:
        return "VS Code"
    if "windowsterminal.exe" in nombres:
        return "Terminal"
    return "Consola"


def nombre_proyecto(cwd: str) -> str:
    if not cwd:
        return "Claude"
    if os.path.normcase(os.path.normpath(cwd)) == os.path.normcase(os.path.expanduser("~")):
        return "~"
    return os.path.basename(os.path.normpath(cwd))


def aplicar(sesion: dict, evento: dict, ahora: float) -> dict:
    """El estado nuevo de la sesión tras el evento (o None si hay que borrarla)."""
    nombre = evento.get("hook_event_name", "")
    if nombre == "SessionEnd":
        return None
    s = dict(sesion)
    # La carpeta donde arrancó la sesión: la actual cambia cuando Claude trabaja en otros sitios.
    cwd = s.get("cwd") or evento.get("cwd") or ""
    s.update(id=evento.get("session_id", s.get("id", "")), cwd=cwd, proyecto=nombre_proyecto(cwd), ts=ahora)
    # La transcripción: de ahí saca la app el consumo de tokens de la sesión.
    if evento.get("transcript_path"):
        s["transcript"] = evento["transcript_path"]
    s.setdefault("estado", "inicio")
    s.setdefault("desde", ahora)

    def pasar(estado: str) -> None:
        if s.get("estado") != estado:
            s["estado"], s["desde"] = estado, ahora

    if nombre == "SessionStart":
        if evento.get("source") != "compact":
            pasar("inicio")
            s.update(actividad="", aviso="")
    elif nombre == "UserPromptSubmit":
        s.update(estado="trabajando", desde=ahora, pedido=recortar(evento.get("prompt", ""), LARGO_PEDIDO),
                 actividad="", aviso="")
    elif nombre == "PreToolUse":
        herramienta = evento.get("tool_name", "")
        if herramienta in PREGUNTAS:
            pasar("esperando")
            s["aviso"] = PREGUNTAS[herramienta]
        else:
            pasar("trabajando")
            s["aviso"] = ""
        s["actividad"] = actividad(herramienta, evento.get("tool_input"))
    elif nombre == "PostToolUse":
        if s.get("estado") == "esperando":
            pasar("trabajando")
            s["aviso"] = ""
    elif nombre == "Notification":
        tipo = evento.get("notification_type", "")
        mensaje = evento.get("message", "")
        # El aviso de «lleva un rato esperando tu mensaje» llega cuando ya terminó: no es una espera nueva.
        if tipo != "idle_prompt" and "waiting for your input" not in mensaje.lower():
            pasar("esperando")
            s["aviso"] = recortar(traducir(mensaje) or "Necesita tu atención", LARGO_ACTIVIDAD)
    elif nombre == "Stop":
        pasar("terminado")
        s.update(actividad="", aviso="")
    return s


def ruta_de(session_id: str) -> str:
    seguro = "".join(c for c in session_id if c.isalnum() or c in "-_") or "sin-id"
    return os.path.join(CARPETA, seguro + ".json")


def buzon_de(session_id: str, extension: str) -> str:
    return os.path.join(BUZON, os.path.basename(ruta_de(session_id))[:-5] + extension)


def cancelar_espera(session_id: str) -> None:
    try:
        os.remove(buzon_de(session_id, ".espera"))
    except OSError:
        pass


def esperar(evento: dict, limite: float, pausa: float = 1.0) -> int:
    """Modo `esperar` (Stop, asyncRewake): se queda mirando el buzón de la sesión. Si el usuario escribe desde Herdview, anota
    el pedido y sale con 2 y el mensaje en stderr, lo que despierta a Claude aunque esté parado. Sale con 0 sin hacer
    nada si otra espera de la misma sesión la sustituye, si el usuario escribe en la propia ventana (UserPromptSubmit borra
    la marca) o al llegar al límite."""
    sid = evento["session_id"]
    os.makedirs(BUZON, exist_ok=True)
    marca, mensajes = buzon_de(sid, ".espera"), buzon_de(sid, ".txt")
    ficha = "%d-%f" % (os.getpid(), time.time())
    with open(marca, "w", encoding="utf-8") as fh:
        fh.write(ficha)
    while time.time() < limite:
        if os.path.exists(mensajes):
            leyendo = "%s.%d" % (mensajes, os.getpid())
            try:
                os.replace(mensajes, leyendo)
                with open(leyendo, encoding="utf-8") as fh:
                    texto = fh.read().strip()
                os.remove(leyendo)
            except OSError:
                texto = ""
            if texto:
                cancelar_espera(sid)
                registrar(dict(evento, hook_event_name="UserPromptSubmit", prompt=texto), time.time())
                sys.stderr.write("Tu usuario te escribe desde Herdview (contéstale como a un mensaje suyo normal):\n\n" + texto)
                return 2
        try:
            with open(marca, encoding="utf-8") as fh:
                if fh.read() != ficha:
                    return 0
        except OSError:
            return 0
        time.sleep(pausa)
    return 0


def registrar(evento: dict, ahora: float) -> None:
    ruta = ruta_de(evento["session_id"])
    if evento.get("hook_event_name") in ("UserPromptSubmit", "SessionEnd"):
        cancelar_espera(evento["session_id"])
    try:
        with open(ruta, encoding="utf-8") as fh:
            sesion = json.load(fh)
    except (OSError, ValueError):
        sesion = {}
    # Los hooks van en segundo plano: uno de un evento anterior puede llegar tarde y no debe pisar al nuevo.
    if sesion.get("ts", 0) > ahora:
        return
    nueva = aplicar(sesion, evento, ahora)
    if nueva is None:
        try:
            os.remove(ruta)
        except OSError:
            pass
        return
    if not nueva.get("procesos"):
        cadena = ancestros()
        nueva.update(procesos=cadena, origen=origen(cadena))
    os.makedirs(CARPETA, exist_ok=True)
    temporal = "%s.%d.tmp" % (ruta, os.getpid())
    with open(temporal, "w", encoding="utf-8") as fh:
        json.dump(nueva, fh, ensure_ascii=False)
    os.replace(temporal, ruta)


def main() -> int:
    evento = json.loads(sys.stdin.buffer.read().decode("utf-8") or "{}")
    if not evento.get("session_id"):
        return 0
    if len(sys.argv) > 1 and sys.argv[1] == "esperar":
        return esperar(evento, time.time() + DURACION_ESPERA)
    registrar(evento, time.time())
    return 0


if __name__ == "__main__":
    # Claude Code lee la salida del hook como UTF-8; en Windows Python escribiría en la página de códigos (é → �).
    sys.stderr.reconfigure(encoding="utf-8")
    try:
        codigo = main()
    except Exception as error:  # nunca molestar a la sesión (un 2 aquí la despertaría con el error)
        print("herdview: %s" % error, file=sys.stderr)
        codigo = 0
    sys.exit(codigo)
