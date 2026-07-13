# 🔥 Crisol

**Mezclador de audio casero para Windows** — un reemplazo mínimo y sin drivers de Voicemeeter.

Marca **fuentes** (lo que suena en un dispositivo vía loopback WASAPI, y/o micrófonos) y
**salidas** (uno o varios dispositivos físicos). Crisol mezcla las fuentes —cada una con su
volumen— y reparte la **misma** mezcla a todas las salidas marcadas, **sincronizadas entre sí**,
con un volumen general al final. Todo en una ventanita que vive en la bandeja del sistema.

> Pensado para "quiero que esto suene a la vez en los monitores, los cascos y el altavoz del
> baño, cada uno a su volumen, sin lag entre ellos y sin instalar un panel de audio pesado".

---

## ✨ Características

- 🎚️ **N fuentes → M salidas** simultáneas, con volumen por fila y volumen maestro.
- 🔊 **Loopback WASAPI**: captura lo que ya suena en cualquier dispositivo de salida (o un micrófono) sin cables ni drivers virtuales.
- 🎯 **Salidas en fase** entre sí, indefinidamente: un resampleador asíncrono por salida compensa la deriva de reloj entre tarjetas (ver *Cómo funciona*).
- 💤 **Aguanta suspender/reanudar y hot-plug**: si Windows invalida los dispositivos, Crisol los reconecta solo.
- 🪟 **Vive en la bandeja**: cerrar la ventana la oculta, la mezcla sigue sonando.
- 🚀 **Inicio con Windows** opcional (arranca oculto en la bandeja).
- 🔁 **Sin eco**: un dispositivo no puede ser fuente y salida a la vez; el lado contrario se bloquea automáticamente.
- 🧪 Botón **Probar** y modos de diagnóstico por línea de comandos.
- 💾 Configuración persistente en `%APPDATA%\Crisol\config.json`.

---

## 📦 Requisitos

- **Windows 10/11** (x64).
- Para **compilar**: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- Para **ejecutar el binario self-contained**: nada — el runtime va incluido.

---

## ▶️ Descargar y ejecutar

Clona y genera un ejecutable **autónomo** (no necesita .NET instalado para correr):

```powershell
git clone https://github.com/Authoritt/Crisol.git
cd Crisol
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

El ejecutable queda en `dist\Crisol.exe` — hazle doble clic. (Es un único archivo grande porque
lleva dentro el runtime de .NET y WPF; si prefieres uno pequeño que dependa del runtime instalado,
cambia `--self-contained true` por `false`.)

Para desarrollar, basta `dotnet run`.

---

## 🎧 Uso típico (modelo Voicemeeter, sin driver)

1. Deja tu dispositivo habitual como **salida por defecto de Windows** (ahí "entra" todo el audio).
2. Márcalo en Crisol como **fuente**.
3. Marca como **salidas** los demás dispositivos donde quieras oír lo mismo, cada uno con su volumen.

Pulsa **Probar** para lanzar un tono a cada salida y verificar la cadena completa.

> **Limitación honesta:** Crisol no puede aparecer como un dispositivo de salida *virtual* propio —
> en Windows eso exige un driver de kernel firmado. Si quieres un endpoint dedicado que no suene
> por sí mismo (estilo *"Voicemeeter Input"*), instala el driver gratuito **VB-CABLE**: aparecerá
> "CABLE Input" y Crisol lo listará automáticamente como fuente (hay un botón que lo instala y lo
> deja configurado por ti).

---

## 🔧 Cómo funciona

```
fuentes (loopback/mic) ──► mezclador 48 kHz estéreo float ──► volumen maestro
                                                                     │
                                        bomba de tiempo real (reloj de pared, trozos de 10 ms)
                                                                     │
                        ┌────────────────────────┼────────────────────────┐
                     salida A                  salida B                  salida C
                   (ASRC + WASAPI)          (ASRC + WASAPI)          (ASRC + WASAPI)
```

- **Una sola bomba** lee la mezcla al ritmo del reloj de pared y escribe los **mismos bytes** en
  todas las salidas: eso las alinea de origen.
- Cada tarjeta consume a **su propio reloj de hardware**, así que con el tiempo derivarían (eco
  entre monitores en la misma sala). Cada salida lleva un **conversor de tasa asíncrono (ASRC)**
  que varía suavemente (±0,4 %) su consumo para mantener su buffer en una latencia objetivo fija,
  anclando todas las salidas al mismo reloj — y por tanto **en fase entre sí**.
- **Recuperación de dispositivos:** al suspender, Windows invalida los clientes WASAPI y NAudio no
  reconecta solo. Crisol escucha `PlaybackStopped`/`RecordingStopped`, `PowerModeChanged` y las
  notificaciones de endpoints, y reconstruye el motor con dispositivos frescos (con reintentos
  acotados y freno para dispositivos que se caen en bucle).

Stack: **WPF + [NAudio](https://github.com/naudio/NAudio)** (WASAPI compartido). Renombrado/ocultado
de endpoints vía COM (`IPropertyStore`/`IPolicyConfig`).

---

## 🩺 Diagnóstico

- `Crisol.exe --selftest` → enumera dispositivos y arranca el motor; escribe `%TEMP%\crisol-selftest.log`.
- `Crisol.exe --diag` → prueba audible + contable (tono por salida, loopback medido, cadena completa) en `%TEMP%\crisol-diag.log`.

Si algo no suena, revisa también `%APPDATA%\Crisol\error.log`.

---

## 📁 Estructura

| Archivo | Rol |
|---|---|
| `AudioEngine.cs` | Motor: fuentes, mezclador, bomba, salidas, ASRC anti-deriva. |
| `MainWindow.xaml(.cs)` | UI, VU meters, bandeja, recuperación de dispositivos, prueba. |
| `AppConfig.cs` | Carga/guardado de `config.json`. |
| `CableInstaller.cs` / `PolicyConfig.cs` | Instalar/activar VB-CABLE y renombrar/ocultar endpoints. |

---

Hecho para uso personal y compartido tal cual, sin garantías. Si te sirve, úsalo. 🔥
