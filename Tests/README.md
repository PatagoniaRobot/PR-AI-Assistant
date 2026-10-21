# Bancos de prueba

Ejercen el **código de producción**: cada banco referencia el DLL ya compilado de la
aplicación e instancia los servicios y ViewModels reales. No hay dobles de la
aplicación acá dentro.

Ese fue un cambio de criterio deliberado. Los bancos anteriores validaban unidades
contra dobles y llegaron a informar 289 aserciones sin fallos mientras el producto
entregaba respuestas vacías y no mostraba los modelos descargados. Un doble modela
lo que uno ya entendió del mundo, no el mundo.

## Correrlos

```bat
:: primero, la aplicación tiene que estar compilada
dotnet build -c Debug
dotnet build -c Forense

cd Tests\SeleccionDeModelos
dotnet run -p:EdicionApp=Debug      :: 13 aserciones
dotnet run -p:EdicionApp=Forense    ::  9 aserciones (el resto no aplica)

cd ..\BorradoDeModelos
dotnet run                          :: 52 aserciones + 12 de humo

cd ..\CargaDeArchivos
dotnet run -p:EdicionApp=Debug      :: 46 aserciones + 11 de humo
dotnet run -p:EdicionApp=Forense    :: igual: la carga no es capacidad de red

cd ..\CarpetasDeModelos
dotnet run -p:EdicionApp=Debug      :: 93 aserciones (incluye humo y cableado)
dotnet run -p:EdicionApp=Forense    :: 55: verifica que NO haya carpetas configurables
dotnet run -- --sin-humo            :: saltea la ventana dibujada

cd ..\ArchivosQueNoSonModelos
dotnet run -p:EdicionApp=Debug      :: 55 aserciones + 5 de humo
dotnet run -p:EdicionApp=Forense    :: 32: la cabecera y el selector; el catálogo no existe
dotnet run -- --con-red             :: además, cinco cabeceras reales de HuggingFace

cd ..\RecomendacionSegunLaPC
dotnet run -p:EdicionApp=Debug      :: 72 aserciones (necesita los modelos locales, si no los omite)
dotnet run -p:EdicionApp=Forense    :: igual: la recomendación no es código de red
dotnet run -- --con-red             :: además, el catálogo contra un repositorio real, con humo

cd ..\PrimerArranque
dotnet run -p:EdicionApp=Debug      :: 18 aserciones + 4 de humo
dotnet run -p:EdicionApp=Forense    :: 14: sin catálogo, la guía manda a copiar el archivo

cd ..\Idiomas
dotnet run -p:EdicionApp=Debug      :: 38: español e inglés parejos, claves, emojis, íconos, reporte, pantallas en inglés
dotnet run -p:EdicionApp=Forense    :: igual, y que no viajen los textos de web, catálogo e imágenes

cd ..\GeneracionDeImagenes
dotnet run                          :: 286 aserciones + 61 de humo (necesita el motor)
dotnet run -- --con-red             :: además, la descarga real del VAE (0,31 GB)
dotnet run -- --sin-alternancias    :: saltea las diez cancelaciones seguidas (~90 s)
powershell .\verificar-barrera.ps1  :: la barrera de E4 sobre el binario Forense
```

Devuelven 0 si pasa todo y 1 si algo falla, así que sirven en un script.

**No están en `PR-AI-Assistant.sln` a propósito.** Referencian el DLL por `HintPath`,
no por `ProjectReference`: en un clone limpio, compilar la solución fallaría por un
binario que todavía no existe. Se corren a mano, después de compilar la aplicación.

## Qué cubre cada uno

### `SeleccionDeModelos` — backlog 9.1 y 9.2

Instancia `ModeSelectionViewModel` y ejerce el ciclo completo del usuario: elegir un
modelo, guardarlo, reabrir la pantalla. Cubre el descubrimiento de la carpeta de
descargas, la persistencia por `SelectionKey`, la exclusión de `mmproj` (el resto de lo
que no es modelo está en `ArchivosQueNoSonModelos`), la carpeta
ausente, la paridad con el selector del chat y el borde Forense.

### `ArchivosQueNoSonModelos` — pendiente 6.5

Que ni el catálogo ni el selector ofrezcan como modelo un `.gguf` que no carga solo:
proyector, cabeza de borrador, adaptador LoRA, solo vocabulario, parte de un modelo
partido. Arma las cabeceras byte a byte con el formato GGUF real, en lugar de bajar
archivos de varios GB, e incluye el peor caso: una cabeza de borrador con nombre de
modelo, que solo la cabecera desenmascara.

El catálogo se ejerce con el `ModelCatalogViewModel` real contra un HuggingFace falso
que responde lo que se midió del verdadero, incluido `Range` sobre `/resolve/`. Verifica
que el disfrazado se frene pidiendo **solo** la cabecera, que lo legítimo se siga bajando
y que, si la cabecera no se puede leer, no se bloquee. Con `--con-red` lee cinco
cabeceras reales, entre ellas la del archivo de 4.22 del maestro.

El humo dibuja la ventana con `RenderTargetBitmap` y deja la captura en
`%TEMP%\catalogo-no-modelos.png`.

### `RecomendacionSegunLaPC` — etapa E5

Que la aplicación recomiende cuantización y parámetros según la PC. Tres partes:

- **La PC**, detectada de verdad contra esta máquina (DXGI, RAM, núcleos físicos, el
  backend de las `.dll` de `Tools\`), y la regla de qué placa sirve con PCs descritas a
  mano: una AMD con el motor CUDA, una integrada, ninguna.
- **La memoria**, contra lo que midió `llama-server` el 20 de octubre de 2026 en
  Gemma 4 E2B, E4B y Qwen3.5-9B a 8k y 32k: pesos en placa y en RAM, caché y estado
  recurrente, al MiB. Usa los `.gguf` de `Models\` y de `ModelosDescargados\`; si no
  están, esos casos se omiten y lo dicen.
- **La recomendación**: qué cuantización y qué parámetros para placas de 16, 8, 6 y 4 GB
  y sin placa, el perfil «Automático» en la pantalla de selección real —incluida la
  migración de un `settings.json` anterior— y, con `--con-red`, el catálogo contra
  `unsloth/Qwen3.5-9B-GGUF`. El humo dibuja la ventana y deja
  `%TEMP%\catalogo-recomendacion.png`.

### `PrimerArranque` — lo que ve quien lo baja

PR-AIA se publica sin modelos. Esto vigila lo que encontró la prueba a mano del 20 de
octubre de 2026: el perfil de entrada era Forense y no dejaba bajar nada, el chat decía
«listo» sin modelo y la pantalla de inicio hablaba de un modelo ilegible. Cubre el perfil
de entrada por edición, la guía con y sin catálogo, el paso a General cuando el perfil no
habilita descargas —y que en Investigación no se ofrezca—, y que la guía desaparezca al
haber un modelo. El humo dibuja el chat y deja `%TEMP%\primer-arranque.png`.

### `BorradoDeModelos` — backlog 9.4

Las tres barreras del borrado (modelo activo, catálogo distribuido, fuera de la
carpeta), el `.part` asociado, el `mmproj` huérfano y el que no lo está, y el
registro de la baja en `model-downloads.jsonl`.

Su `Humo.cs` **abre la ventana del catálogo de verdad**, fuerza el layout y escucha
`PresentationTraceSources.DataBindingSource`, que es donde WPF deja los errores de
binding que por defecto se traga en silencio. Además lee el texto efectivamente
dibujado en el árbol visual, no solo que los controles existan: encontró una fila
que salía con el color correcto y el texto equivocado. Deja una captura de la
ventana en `%TEMP%\catalogo-con-descargados.png` — conviene mirarla.

### `CargaDeArchivos` — backlog 9.3

La regla de precedencia de la capacidad —el Modo Investigación la **habilita** en vez
de quitarla, al revés que la búsqueda web y las descargas— y, sobre todo, que un
archivo adjuntado en Modo General no se convierta en evidencia: no entra al system
prompt, no aparece en el panel de contexto forense y del log solo queda el hecho, no
el contenido. Verifica también que Investigación siga comportándose igual que antes.

Instancia un `MainViewModel` real con un doble del diálogo de archivos. No necesita
servidor de inferencia: para saber a dónde fue a parar el archivo alcanza con mirar
el system prompt del orquestador y las colecciones del ViewModel.

Su `Humo.cs` dibuja la vista de chat y ejercita la tira de adjuntos desde el árbol
visual: pulsa la ✕ real, no el comando del ViewModel. Deja una captura en
`%TEMP%\tira-de-adjuntos.png`.

**Una lección de este banco, que vale para los que vengan.** La aserción «el
contenido del archivo no está en el log» pasaba, y pasaba porque el log se leía
vacío: el `ForensicLogger` mantiene el archivo abierto en escritura y
`File.ReadAllText` chocaba con eso, pero la `IOException` estaba atrapada y devolvía
cadena vacía. Una aserción negativa sobre una fuente vacía siempre pasa. Ahora la
lectura falla ruidosamente si no puede leer.

### `CarpetasDeModelos` — dónde viven los modelos y las imágenes

Cubre la configuración de las tres carpetas de datos y el movimiento de lo que quedó
atrás. Lo que verifica no es que mueva archivos, sino las tres decisiones que pueden
perder trabajo del usuario:

- **Cambiar la carpeta NO mueve nada.** Es una redirección instantánea que no puede
  fallar; mover es una acción aparte que el usuario pide y puede cancelar. Si las dos se
  juntaran, un movimiento cortado a la mitad dejaría la configuración a medias.
- **Entre volúmenes se copia, se verifica por SHA-256 y recién ahí se borra el origen.**
  Mientras el destino no esté completo y promovido a su nombre final, el original sigue
  intacto. Ese camino **no se ejerce en una sola máquina por casualidad**:
  `FolderMigrator` decide por la raíz de la ruta, así que el banco monta una unidad
  virtual con `subst`. Sin eso, la parte con riesgo real quedaría sin probar y las
  aserciones pasarían igual — la misma clase de agujero que el descriptor recortado de
  E4.3.
- **Un archivo que ya existe en el destino no se pisa nunca**, y tampoco se borra el de
  la carpeta anterior: saltear no puede significar perderlo.

`Cableado.cs` instancia un `MainViewModel` real. Es la lección de 4.19: el resto del
banco construye el servicio de carpetas a mano, con la instancia de imágenes ya
conectada, así que ejerce el camino bueno por construcción. Quien lo arma de verdad es
`MainViewModel`, y esa costura solo existe ahí.

`Humo.cs` abre la ventana y deja dos capturas —`ventana-de-carpetas-aviso.png` y
`ventana-de-carpetas.png`—. **Hay que mirarlas**, y ya valieron la pena: la ruta larga
quedaba cortada contra el borde de su campo, que es lo peor que puede pasarle a una
ventana cuyo contenido *son* las rutas. Ninguna aserción lo veía porque un `TextBox` no
recorta el texto, lo desplaza: `Text` sigue entero.

**Tres cosas que este banco aprendió mutando, y las tres son la misma trampa de siempre.**

1. **Una aserción de dibujo medida en el momento equivocado no prueba nada.** La
   comprobación de rutas cortadas corría *después* de cambiar la carpeta, cuando lo que
   se dibuja es una ruta corta de `%TEMP%` que entra de sobra: pasaba con
   `TextWrapping="NoWrap"`, o sea sobre el defecto exacto que existe para detectar.
   Movida a donde está la ruta larga, falla diciendo «necesita 659px y tiene 628px».
   Es la misma forma que el progreso medido solo a 1024×1024.
2. **Poner la reapuntada de paquetes en `Set()` no hizo fallar ninguna aserción**, y
   revisarlo valió más que el arreglo: la invariante «cambiar la carpeta no rompe el
   paquete» está protegida por **dos** cosas independientes —no reapuntar al cambiar, y
   exigir que el archivo esté del otro lado— así que una aserción sobre el resultado pasa
   mientras cualquiera de las dos siga en pie. Se agregó una que mira el mecanismo, con
   su control positivo; quitando el guarda de existencia, fallan cuatro.
3. **El humo fabricaba un estado que la aplicación nunca produce.** Seteaba
   `HasPendingMove` a mano y el botón salía deshabilitado, porque el comando solo
   reevalúa su `CanExecute` cuando el ViewModel lo pide. Habría reportado un defecto
   inexistente. Ahora ejerce el comando que aprieta el usuario.

Mutaciones que sí muerden: saltear la colisión, borrar el origen antes de tener la copia
buena, y quitar el guarda de existencia al reapuntar — 4, 4 y 4 aserciones.

### `GeneracionDeImagenes` — E4.1 a E4.5

Ejerce `ImageServerManager` e `ImageGenClient` contra `sd-server` de verdad. Lo que
verifica no es que genere una imagen —eso ya se sabía del reconocimiento de E4.0— sino
las tres cosas que la API del motor **no** da y hubo que resolver afuera:

- **El progreso sale de stdout, y hay tres barras que se dibujan igual.** La respuesta
  del job no tiene ningún campo de avance, así que hay que parsear la salida — y el motor
  imprime `%s %i/%i - %s` para el muestreo, para la carga de tensores y para la
  decodificación del VAE en tiles:

  ```
    |####| 108/386 - 2.34GB/s     carga de tensores
    |####| 3/8     - 1.84s/it     MUESTREO — la que importa
    |####| 47/49   - 14.08it/s    decodificación del VAE en tiles (7x7)
  ```

  **De la línea sola no se distinguen.** La unidad tampoco alcanza: el muestreo usa
  `s/it` o `it/s` según si la iteración pasa del segundo. El patrón original solo aceptaba
  `s/it` y por eso funcionaba a 1024×1024 y **perdía el progreso entero a 512×512**; al
  aceptar las dos, entró la barra del VAE y el avance saltaba de 8 a 49. El discriminador
  correcto es el número de pasos que se pidió, que lo declara `ImageGenClient` con
  `BeginJob`. Las dos trampas tienen su aserción, y las dos unidades se prueban sin motor
  contra líneas reales capturadas de la salida.
- **Cancelar exige matar el proceso.** Un job que ya está generando devuelve 409. Se
  comprueba que la cancelación corte, que no deje la imagen escrita, y que el motor
  vuelva solo y pueda generar de nuevo — que es donde aparecería el puerto ocupado si
  el relanzamiento no esperara la salida real del proceso.
- **Los fallos son datos.** Sin motor, sin modelo, prompt vacío y puerto tomado por otro
  modelo devuelven un mensaje legible en lugar de una excepción.

`Arbitro.cs` cubre el semáforo de VRAM de E4.2, y sus casos **no son inventados**: la
tabla de pisos por resolución sale de medir el motor en esta máquina bajo presión del
modelo de lenguaje, y las aserciones verifican que la estimación se parezca a lo
observado **y que no lo subestime** — subestimar haría que el semáforo diga verde y la
generación falle, que es el error que importa evitar.

Lo que ese banco protege, y que no es evidente:

- **A 4096 píxeles de lado el motor no se queda sin memoria: se muere.**
  `CUDA error: invalid configuration argument`, reproducido con 15.355 MiB libres, o sea
  con la GPU vacía. Por eso el veredicto `Blocked` está separado de `Red`: uno es "va a
  fallar y se recupera", el otro es "no lo intentes". El límite se comprueba además en
  `ImageGenClient`, no solo en el semáforo, porque un aviso que el llamador pueda
  ignorar no alcanza para algo que mata el proceso.
- **`/sdcpp/v1/capabilities` anuncia `max_width` 4096**, justo el valor que se cae. Los
  límites que declara el motor no se pueden usar tal cual para poblar la interfaz.
- **El semáforo mira la VRAM libre, no la total.** Es lo que lo distingue del de modelos
  de lenguaje: acá siempre hay algo más cargado.

`Paquetes.cs` y `HumoPaquetes.cs` cubren E4.3: el paquete de tres archivos, la
persistencia, la descarga y el selector.

- **La descarga real se ejerce con `--con-red`**, y baja **solo el VAE** (0,31 GB) con
  los otros dos componentes puestos como archivos de mentira. Alcanza para recorrer el
  camino entero —salteo de lo existente, subcarpeta del repositorio, `.part`, armado del
  paquete— sin bajar 6,5 GB en cada corrida. Ojo con recortar el descriptor para eso:
  una primera versión usaba una fuente con **un solo componente** y eso escondió un
  defecto real, porque el armado mapeaba por posición y le asignaba el VAE al hueco de
  la difusión. **Un descriptor recortado no es el descriptor.**
- `HumoPaquetes.cs` abre la ventana de verdad y deja una captura en
  `%TEMP%\selector-de-paquetes.png`. **Hay que mirarla**: las 15 aserciones de la primera
  versión pasaron sobre una ventana donde el estado del paquete incompleto salía cortado
  —decía «faltan modelo de difusión, text encoder,» y ahí terminaba—. La aserción
  buscaba puntos suspensivos, y el `DataGrid` recorta **sin ponerlos**. Ahora se mide el
  ancho natural del texto con `FormattedText` contra el `ActualWidth` del control, que es
  la única forma confiable; con la columna vuelta a 210 px, la aserción falla diciendo
  «necesita 235px y tiene 210px».

`Generacion.cs` y `HumoGeneracion.cs` cubren E4.4: la vista de generación.

- **El hilo de UI se mide, no se supone.** `ContextoDeUnHilo` es un
  `SynchronizationContext` de un solo hilo que cronometra cada continuación mientras el
  ViewModel real genera contra el motor real. Es la misma herramienta que encontró el
  defecto 4.10 del maestro. Imprime el máximo y el total: hoy 7 ms de bloqueo máximo y
  35 ms sobre 10,5 s de generación.
- **Diez alternancias de generar y cancelar**, que era un criterio de E4.2 que había
  quedado sin cumplir. Se salta con `--sin-alternancias` cuando se necesita una corrida
  rápida; son ~90 s.
- **La metadata se verifica ida y vuelta contra el disco**, y se comprueba que la semilla
  guardada sea la que produjo la imagen — el motor no informa cuál usó, así que mandarle
  `-1` daría un archivo irreproducible.
- `HumoGeneracion.cs` abre la vista de verdad y deja `%TEMP%\vista-de-generacion.png`.
  **Hay que mirarla.** Las 25 aserciones de la primera versión pasaban sobre una vista en
  la que, con los ajustes avanzados desplegados, **el botón de generar quedaba abajo del
  scroll**, y donde la galería vacía ocupaba una franja muerta. Ninguna aserción lo vio.

`Integracion.cs` y `HumoBarraLateral.cs` cubren E4.5: la capacidad del perfil y la entrada
en la barra lateral.

- **La edición institucional se prueba de verdad, no por lectura del código.** El banco
  carga `bin\Forense\...\PR-AI-Assistant.dll` por reflexión y ejerce
  `EffectiveProfile.Resolve` con un perfil que trae `image_gen_enabled: true`. Tiene que
  dar false. Con control positivo sobre el binario comercial, que tiene que dar true: sin
  eso la aserción pasaría igual si `Resolve` devolviera false siempre.
- **Eso corre en un PROCESO APARTE**, y no es prolijidad. Los dos binarios se llaman
  `PR-AI-Assistant` y tienen la misma versión, así que con los dos en memoria WPF resuelve
  mal los pack URI y las pruebas de humo posteriores fallan con «No se encuentra el
  recurso …imagepackageselectorwindow.xaml», hablando de una ventana que no tiene nada que
  ver. Un `AssemblyLoadContext` colectible no alcanza: aísla los tipos, pero la descarga
  es diferida y WPF ya se confundió.
- **Y antes de eso hay otra trampa:** `Assembly.LoadFrom` devuelve el ensamblado **ya
  cargado** cuando la identidad coincide, así que la prueba habría corrido contra el
  binario comercial sin mirar nunca el institucional. Por eso se verifica que
  `asm.Location` sea la ruta esperada y que ahí **no exista** `ImageServerManager`.
- `HumoBarraLateral.cs` abre `ChatView` de verdad y deja
  `%TEMP%\barra-lateral-imagenes.png`. **La barra lateral arranca cerrada**: la primera
  versión no la abría y tres aserciones de visibilidad pasaban leyendo una pantalla que no
  se estaba dibujando. Ahora se abre y hay un control positivo que lo comprueba.

**Dos trampas de WPF que costaron entender, anotadas para no repetirlas:**

- **`Visibility` no responde «¿lo ve el usuario?».** Es la propiedad local del control,
  así que algo adentro de un contenedor colapsado informa `Visible` igual. La que
  corresponde es **`IsVisible`**, que mira a los ancestros. Y para saber si algo quedó
  fuera del área de un `ScrollViewer` no sirve ninguna de las dos: hay que comparar su
  rectángulo contra el de la ventana, como hace `EstaDentro`.
- **Cerrar la ventana del primer humo apaga la aplicación.** `ShutdownMode` es
  `OnLastWindowClose` de fábrica, y con la `Application` apagándose la ventana siguiente
  se muestra sin lanzar pero **no arma su árbol visual**: todo se busca y no se encuentra.
  Por eso existe `EntornoWpf.Preparar()`, que lo fija una sola vez; hacerlo tarde lanza.

Necesita el motor instalado. Por defecto lo busca en una carpeta `sd` al lado del repositorio; se
cambia con la variable de entorno `SD_RAIZ`. Sin motor, informa cuántas pruebas omitió
en lugar de pasar en silencio.

`verificar-barrera.ps1` busca los símbolos de E4 como **bytes, en cualquier alineación**
y en UTF-8 y UTF-16, con control positivo sobre el binario Debug. Con la barrera puesta
da 0 fallas; desmontándola a propósito en el `.csproj`, da 8.

**Dos lecciones de este banco, las dos encontradas mutando y no escribiendo.**

La primera: *una aserción puede pasar por el motivo equivocado*. «Prompt vacío no
genera» pasaba aun quitando la validación, porque el banco corría sin servidor y frenaba
en el chequeo de «motor no listo». Ahora corre **contra un servidor listo**, que es la
única forma de que la validación sea lo único que pueda detenerlo.

La segunda: *`Dispose()` no prueba el `JobObject`*. La aserción de huérfanos pasaba con
el `JobObject` comentado, porque `Dispose()` mata el proceso explícitamente. Para
ejercer «matar la aplicación a lo bruto» hace falta un proceso aparte —el banco se
relanza a sí mismo con `--hijo-que-muere`— al que se le hace `Kill(entireProcessTree:
false)`. Si `sd-server` sobrevive a eso, es que no está dentro de ningún `JobObject`.

## Cómo se comprueba que un banco sirve

Un banco que pasa entero a la primera todavía no probó nada. Las dos formas de
comprobar que muerde, las dos usadas al escribir estos:

**Contra el código anterior.** `-p:RaizApp=` apunta el banco a la carpeta de la
aplicación de otro árbol (por defecto, `src\PR-AI-Assistant` de este repo), así que se
lo corre contra un worktree en el commit previo al arreglo:

```bat
git worktree add ..\praia-baseline <commit-anterior> --detach
cd ..\praia-baseline
dotnet build -c Debug

cd <repo>\Tests\SeleccionDeModelos
dotnet run -p:EdicionApp=Debug -p:RaizApp=<ruta-absoluta>\praia-baseline\src\PR-AI-Assistant
```

Los commits anteriores al 21 de octubre de 2026 tienen la aplicación en la raíz: para
esos, `RaizApp` es la raíz del worktree.

**`RaizApp` tiene que ser una ruta absoluta.** MSBuild resuelve las relativas contra
la carpeta del banco, no contra el directorio actual, y un `HintPath` que no existe
no lo hace fallar: omite la referencia y el banco explota con decenas de «no se
encontró el tipo `PRImager`». Los `.csproj` verifican que el DLL esté y cortan con
ese motivo, pero conviene saberlo.

Contra `f6c18fd` este banco da 7 OK y 5 fallas, y reproduce el síntoma que había
reportado el usuario. Después del arreglo, 13 y 0.

Terminado, se limpia el worktree:

```bat
git worktree remove ..\praia-baseline --force
```

**Mutando el código.** Se desactiva a propósito la barrera que el banco dice cubrir y
se comprueba que las aserciones fallan. Desactivar las dos barreras críticas del
borrado hace fallar 6 aserciones y borra el modelo activo de verdad. En
`GeneracionDeImagenes`, mutar las cuatro decisiones centrales del motor hace fallar 7
aserciones —y fue así como se descubrió que dos de ellas no probaban nada—; mutar las
tres del semáforo hace fallar 18.

**Cuidado al revertir una mutación con `Copy-Item`.** Restaurar un respaldo devuelve
también su fecha de modificación, más vieja que el DLL que se compiló a partir del
código mutado. MSBuild ve el fuente «al día» y **no recompila**: el banco vuelve a
correr contra el binario mutado y uno cree estar mirando el código sano. Pasó al
escribir `GeneracionDeImagenes`. Después de revertir, tocar la fecha:

```powershell
Get-ChildItem Services\ImageGen\*.cs | ForEach-Object { $_.LastWriteTime = Get-Date }
```

**Y no edites archivos con acentos usando `Get-Content -Raw | ... | Set-Content`.** En
PowerShell 5.1 la lectura toma la codificación ANSI y la escritura pone UTF-8: el
round-trip convierte `difusión` en `difusiÃ³n` y `—` en `â€”`, en silencio y en todo el
archivo. Pasó al ajustar el XAML del selector. Para editar código, usar un editor.

## Al agregar un banco nuevo

`Tests\**` está excluido del `.csproj` de la aplicación. Sin esas exclusiones el SDK
tomaría estos `.cs` por los globs implícitos y los compilaría dentro del ejecutable
que se entrega —con dos `Main`, y nombrando tipos que en la edición Forense no deben
existir. Si se agrega una carpeta acá, no hace falta tocar nada; si se agrega un
banco fuera de `Tests\`, sí.
