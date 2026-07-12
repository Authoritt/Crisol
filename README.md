# Crisol

Mezclador de audio casero para Windows (reemplazo mínimo de Voicemeeter).

**Qué hace:** marcas **fuentes** (lo que suena en un dispositivo, vía loopback WASAPI, y/o
micrófonos) y **salidas** (uno o varios dispositivos físicos). Crisol mezcla las fuentes
(cada una con su volumen) y reparte la MISMA mezcla a todas las salidas marcadas (cada una
con su volumen), sincronizadas entre sí por una bomba de tiempo real. Volumen general al final.

- WPF + NAudio (WASAPI compartido, mezcla a 48 kHz estéreo float). Sin drivers virtuales.
- Vive en la bandeja del sistema: la X oculta la ventana, la mezcla sigue sonando.
- Checkbox **"Iniciar con Windows"** → clave `Run` de `HKCU` con `--tray` (arranca oculto).
- Config persistente en `%APPDATA%\Crisol\config.json`.
- Un dispositivo no puede ser fuente y salida a la vez (se bloquea el lado contrario: evita eco).
- Deriva de reloj entre dispositivos: si un buffer pasa de 250 ms se resincroniza solo
  (micro-salto audible, latencia acotada ~120-170 ms).

## Uso típico (modelo Voicemeeter, sin driver)
Deja tu dispositivo habitual como salida por defecto de Windows (ahí "entra" todo el audio),
márcalo en Crisol como **fuente**, y marca como **salidas** los demás dispositivos donde
quieras oír lo mismo, cada uno con su volumen.

**Limitación honesta:** Crisol no puede aparecer como un dispositivo de salida virtual propio —
en Windows eso exige un driver de kernel firmado. Si quieres un endpoint dedicado que no suene
por sí mismo (estilo "Voicemeeter Input"), instala el driver gratuito VB-CABLE: aparecerá
"CABLE Input" y Crisol lo listará automáticamente como fuente.

## Build
```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```
El exe queda en `dist\Crisol.exe`. Modo verificación: `Crisol.exe --selftest`
(escribe `%TEMP%\crisol-selftest.log`).
