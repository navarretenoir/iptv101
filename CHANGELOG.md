# Changelog

Todas las versiones notables de IPTV 101 se documentan en este archivo.
El formato sigue aproximadamente [Keep a Changelog](https://keepachangelog.com/es-ES/1.0.0/).

## [1.0.0] — 2026-11-23

Primera versión pública.

### Agregado
- Reproductor IPTV para Windows (WPF + mpv/D3D11): pantalla completa, control remoto/teclado
  multimedia, favoritos, recientes, búsqueda, selector de aspecto, información técnica en pantalla.
- Importador de listas: agregar una lista desde un archivo `.m3u`/`.m3u8` local o desde una URL
  remota, con manejo de errores claro. Pantalla "Tus listas" para ver, actualizar o quitar cada
  lista importada. **IPTV 101 se instala sin ningún canal precargado.**
- Estado vacío explícito en la pantalla principal cuando todavía no se importó ninguna lista.
- 8 apariencias ("skins") de distintas épocas de Windows, aplicables en caliente desde el menú
  "···" → Apariencia, sin reiniciar la app: Windows 95, 98, Me, 2000, XP, 7, 8 y 10/11.
- Ícono propio (SVG + ICO multirresolución 16–256 + PNG 512) en `iconos/`.
- Ventana "Acerca de IPTV 101" con atajos de teclado/control remoto y autoría visible.

### Pendiente (ver sección "Hoja de ruta" del README)
- Versión para Android.
- Versión para las principales distribuciones de Linux.
