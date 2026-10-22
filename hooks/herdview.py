"""Hook de Claude Code para Herdview: anota en un JSON por sesión qué está haciendo cada instancia de Claude.

Se registra (asíncrono) en SessionStart, UserPromptSubmit, PreToolUse, PostToolUse, Notification, Stop y SessionEnd:

    python -I <ruta>/herdview.py            (y en Stop, además: python -I <ruta>/herdview.py esperar)

Lee el evento de stdin y escribe <raíz>/sesiones/<session_id>.json, que la app lee. Un fallo aquí nunca debe molestar
a la sesión: siempre sale con 0 (salvo `esperar`, que sale con 2 a propósito para despertarla).

La raíz es %LOCALAPPDATA%\\Herdview en Windows y ~/.herdview en Linux (dentro de un contenedor de Docker, donde
Herdview lo lee con `docker exec`). Ahí, además, el hook cuenta los tokens (la transcripción no se ve desde Windows) y
tiene tres órdenes para la app: `listar` (sesiones vivas, una por línea), `enviar <sesión>` (mensaje por stdin al
buzón) e `instalar` (se registra en la configuración de Claude Code del contenedor).
"""
import json
import os
import re
import sys
import time

WINDOWS = os.name == "nt"
RAIZ = os.environ.get("HERDVIEW_DIR") or (
    os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "Herdview") if WINDOWS
    else os.path.join(os.path.expanduser("~"), ".herdview"))
CARPETA = os.path.join(RAIZ, "sesiones")
## Lo que el usuario escribe desde Herdview: <sesión>.txt; <sesión>.espera marca la espera activa (una por sesión).
BUZON = os.path.join(RAIZ, "buzon")
## Estado de la cuenta de tokens por sesión (solo fuera de Windows).
CONSUMO = os.path.join(RAIZ, "consumo")
## Algo menos que el timeout del hook en settings (43200 s): al llegar, la espera acaba sola sin error.
DURACION_ESPERA = 43000
LARGO_PEDIDO = 240
LARGO_ACTIVIDAD = 120

## Herramientas que dejan a la sesión esperando al usuario aunque no salte la notificación de permiso.
PREGUNTAS = {"AskUserQuestion": "Te hace una pregunta", "ExitPlanMode": "Espera que apruebes el plan"}


def ultimo(ruta: str) -> str:
    """La última parte de una ruta, sea de Windows o de Linux (el hook corre en los dos)."""
    return re.split(r"[\\/]", (ruta or "").rstrip("\\/"))[-1]


def recortar(texto: str, largo: int) -> str:
    texto = " ".join((texto or "").split())
    return texto if len(texto) <= largo else texto[: largo - 1] + "…"


def actividad(herramienta: str, entrada: dict) -> str:
    """Una línea legible de lo que hace la herramienta, como se vería en la consola."""
    e = entrada or {}
    ruta = e.get("file_path") or e.get("notebook_path") or e.get("path") or ""
    nombre = ultimo(ruta)
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
    return ancestros_windows(limite) if WINDOWS else ancestros_linux(limite)


def ancestros_linux(limite: int) -> list:
    """Lo mismo leyendo /proc (dentro de un contenedor: los PID son los del contenedor)."""
    cadena, pid = [], os.getppid()
    while pid > 0 and len(cadena) < limite:
        try:
            with open("/proc/%d/stat" % pid, encoding="utf-8", errors="replace") as fh:
                stat = fh.read()
        except OSError:
            break
        # «pid (nombre) estado ppid …»: el nombre puede llevar espacios y paréntesis, se corta en el último «)».
        nombre = stat[stat.index("(") + 1: stat.rindex(")")]
        cadena.append({"pid": pid, "exe": nombre})
        pid = int(stat[stat.rindex(")") + 2:].split()[1])
    return cadena


def ancestros_windows(limite: int) -> list:
    import ctypes
    import ctypes.wintypes as w

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


def cuenta(config_dir: str) -> str:
    """La cuenta de Claude Code de la sesión, por su carpeta de configuración: «.claude-alt» → «alt»; sin
    CLAUDE_CONFIG_DIR (o «.claude») → «normal»."""
    nombre = ultimo(config_dir) or ".claude"
    if nombre.startswith(".claude-"):
        return nombre[len(".claude-"):]
    return "normal" if nombre == ".claude" else nombre


def en_contenedor() -> bool:
    return os.path.exists("/.dockerenv") or os.environ.get("container") in ("docker", "podman")


def origen(cadena: list) -> str:
    if not WINDOWS:
        return "Docker" if en_contenedor() else "Linux"
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
    return ultimo(cwd)


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
                    texto = fh.read().lstrip("﻿").strip()
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
    if not nueva.get("cuenta"):
        nueva["cuenta"] = cuenta(os.environ.get("CLAUDE_CONFIG_DIR", ""))
    if not WINDOWS:
        import socket
        nueva["contenedor"] = socket.gethostname()
        claude = next((p["pid"] for p in nueva.get("procesos", []) if p["exe"] in ("claude", "node")), 0)
        nueva["pid_claude"] = claude
        # Desde Windows no se ve la transcripción: los tokens los cuenta el hook (solo lo nuevo cada vez).
        if nueva.get("transcript") and evento.get("hook_event_name") in ("Stop", "PreToolUse", "SessionStart"):
            nueva["consumo"] = medir(evento["session_id"], nueva["transcript"])
    escribir_json(ruta, nueva)


def escribir_json(ruta: str, datos) -> None:
    os.makedirs(os.path.dirname(ruta), exist_ok=True)
    temporal = "%s.%d.tmp" % (ruta, os.getpid())
    with open(temporal, "w", encoding="utf-8") as fh:
        json.dump(datos, fh, ensure_ascii=False)
    os.replace(temporal, ruta)


USO = [re.compile(r'"%s"\s*:\s*(\d+)' % c) for c in
       ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens")]
ID_RESPUESTA = re.compile(r'"id"\s*:\s*"(msg_[^"]+)"')
MODELO = re.compile(r'"model"\s*:\s*"([^"]+)"')


def leer_transcript(estado: dict, ruta: str) -> None:
    """Suma lo nuevo de una transcripción al estado {pos, resto, usos{id: [4]}, ultima[4], modelo}. Cada respuesta
    sale en varias líneas (una por bloque) con el mismo id y uso: se cuenta una vez. Igual que LectorTranscript (C#)."""
    try:
        with open(ruta, "rb") as fh:
            if os.fstat(fh.fileno()).st_size < estado.get("pos", 0):
                estado.clear()
            fh.seek(estado.get("pos", 0))
            datos = fh.read()
    except OSError:
        return
    estado["pos"] = estado.get("pos", 0) + len(datos)
    lineas = (estado.get("resto", "") + datos.decode("utf-8", errors="replace")).split("\n")
    estado["resto"] = lineas.pop()
    usos = estado.setdefault("usos", {})
    for linea in lineas:
        i = linea.find('"usage"')
        if i < 0 or '"assistant"' not in linea:
            continue
        m = ID_RESPUESTA.search(linea)
        if not m:
            continue
        trozo = linea[i:i + 1500]
        valores = [int(r.search(trozo).group(1)) if r.search(trozo) else 0 for r in USO]
        usos[m.group(1)] = valores
        modelo = MODELO.search(linea)
        if '"isSidechain":true' not in linea and modelo and modelo.group(1) != "<synthetic>":
            estado["modelo"], estado["ultima"] = modelo.group(1), valores


def medir(session_id: str, transcript: str) -> dict:
    """Tokens de la sesión con sus subagentes, guardando por dónde va cada fichero para la vez siguiente."""
    ruta_estado = os.path.join(CONSUMO, os.path.basename(ruta_de(session_id)))
    try:
        with open(ruta_estado, encoding="utf-8") as fh:
            estados = json.load(fh)
    except (OSError, ValueError):
        estados = {}
    ficheros = [transcript]
    sub = os.path.join(os.path.splitext(transcript)[0], "subagents")
    if os.path.isdir(sub):
        ficheros += sorted(os.path.join(sub, f) for f in os.listdir(sub) if f.endswith(".jsonl"))
    total = {"entrada": 0, "cache_escrita": 0, "cache_leida": 0, "salida": 0, "respuestas": 0}
    for f in ficheros:
        estado = estados.setdefault(f, {})
        leer_transcript(estado, f)
        for v in estado.get("usos", {}).values():
            for k, n in zip(("entrada", "cache_escrita", "cache_leida", "salida"), v):
                total[k] += n
        total["respuestas"] += len(estado.get("usos", {}))
    principal = estados.get(transcript, {})
    total["contexto"] = sum(principal.get("ultima", [0, 0, 0, 0])[:3])
    total["modelo"] = principal.get("modelo", "")
    escribir_json(ruta_estado, estados)
    return total


def proceso_vivo(pid: int) -> bool:
    """El Claude de la sesión sigue vivo (y es Claude: los PID se reutilizan)."""
    try:
        with open("/proc/%d/comm" % pid, encoding="utf-8") as fh:
            return fh.read().strip() in ("claude", "node")
    except (OSError, ValueError):
        return False


def listar(ahora: float, gracia: float = 20) -> list:
    """Las sesiones vivas (orden `listar`, la usa Herdview por docker exec); borra las de Claudes que ya no existen."""
    vivas = []
    if not os.path.isdir(CARPETA):
        return vivas
    for nombre in sorted(os.listdir(CARPETA)):
        if not nombre.endswith(".json"):
            continue
        ruta = os.path.join(CARPETA, nombre)
        try:
            with open(ruta, encoding="utf-8") as fh:
                s = json.load(fh)
        except (OSError, ValueError):
            continue
        pid = s.get("pid_claude") or 0
        if pid and not proceso_vivo(pid) and ahora - s.get("ts", 0) > gracia:
            try:
                os.remove(ruta)
            except OSError:
                pass
            continue
        vivas.append(s)
    return vivas


def enviar(session_id: str, texto: str) -> None:
    """Orden `enviar`: deja el mensaje en el buzón de la sesión (se suma a lo que aún no haya recogido)."""
    texto = texto.lstrip("﻿")  # .NET Framework antepone la marca BOM al escribir por la entrada de docker
    ruta = buzon_de(session_id, ".txt")
    previo = ""
    try:
        with open(ruta, encoding="utf-8") as fh:
            previo = fh.read().strip()
    except OSError:
        pass
    os.makedirs(BUZON, exist_ok=True)
    temporal = "%s.%d.tmp" % (ruta, os.getpid())
    with open(temporal, "w", encoding="utf-8") as fh:
        fh.write(previo + "\n\n" + texto.strip() if previo else texto.strip())
    os.replace(temporal, ruta)


def instalar(config_dir: str, script: str, python: str = "python3") -> str:
    """Orden `instalar`: registra los hooks de Herdview en settings.json de Claude Code (sin tocar los que haya)."""
    ruta = os.path.join(config_dir, "settings.json")
    try:
        with open(ruta, encoding="utf-8") as fh:
            ajustes = json.load(fh)
    except (OSError, ValueError):
        ajustes = {}
    hooks = ajustes.setdefault("hooks", {})
    orden = '%s "%s"' % (python, script)
    for evento in ("SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"):
        grupos = hooks.setdefault(evento, [])
        if not any(h.get("command") == orden for g in grupos for h in g.get("hooks", [])):
            grupos.append({"hooks": [{"type": "command", "command": orden, "timeout": 20, "async": True}]})
    if not any(h.get("command") == orden + " esperar" for g in hooks["Stop"] for h in g.get("hooks", [])):
        hooks["Stop"].append({"hooks": [{"type": "command", "command": orden + " esperar", "timeout": 43200,
                                         "asyncRewake": True}]})
    escribir_json(ruta, ajustes)
    return ruta


def main() -> int:
    orden = sys.argv[1] if len(sys.argv) > 1 else ""
    if orden == "listar":
        for s in listar(time.time()):
            print(json.dumps(s, ensure_ascii=False))
        return 0
    if orden == "enviar":
        enviar(sys.argv[2], sys.stdin.buffer.read().decode("utf-8"))
        return 0
    if orden == "instalar":
        config = os.environ.get("CLAUDE_CONFIG_DIR") or os.path.join(os.path.expanduser("~"), ".claude")
        print(instalar(config, os.path.abspath(__file__), "python3" if not WINDOWS else "python -I"))
        return 0
    evento = json.loads(sys.stdin.buffer.read().decode("utf-8") or "{}")
    if not evento.get("session_id"):
        return 0
    if orden == "esperar":
        return esperar(evento, time.time() + DURACION_ESPERA)
    registrar(evento, time.time())
    return 0


if __name__ == "__main__":
    # Claude Code lee la salida del hook como UTF-8; en Windows Python escribiría en la página de códigos (é → �).
    sys.stderr.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        codigo = main()
    except Exception as error:  # nunca molestar a la sesión (un 2 aquí la despertaría con el error)
        print("herdview: %s" % error, file=sys.stderr)
        codigo = 0
    sys.exit(codigo)
