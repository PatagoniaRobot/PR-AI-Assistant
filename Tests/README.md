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

cd ..\GeneracionDeImagenes
dotnet run                          :: 61 aserciones (necesita el motor instalado)
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
descargas, la persistencia por `SelectionKey`, la exclusión de `mmproj`, la carpeta
ausente, la paridad con el selector del chat y el borde Forense.

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

### `GeneracionDeImagenes` — E4.1

Ejerce `ImageServerManager` e `ImageGenClient` contra `sd-server` de verdad. Lo que
verifica no es que genere una imagen —eso ya se sabía del reconocimiento de E4.0— sino
las tres cosas que la API del motor **no** da y hubo que resolver afuera:

- **El progreso sale de stdout.** La respuesta del job no tiene ningún campo de avance.
  La aserción que importa es que el progreso **ignore la barra de carga de tensores**,
  que el motor dibuja casi igual pero con cientos de pasos y unidad `GB/s` en lugar de
  `s/it`. Sin anclar la unidad en el patrón, el avance salta a 452 y vuelve a 8.
- **Cancelar exige matar el proceso.** Un job que ya está generando devuelve 409. Se
  comprueba que la cancelación corte, que no deje la imagen escrita, y que el motor
  vuelva solo y pueda generar de nuevo — que es donde aparecería el puerto ocupado si
  el relanzamiento no esperara la salida real del proceso.
- **Los fallos son datos.** Sin motor, sin modelo, prompt vacío y puerto tomado por otro
  modelo devuelven un mensaje legible en lugar de una excepción.

Necesita el motor instalado. Por defecto lo busca en `D:\PROYECTOS\PR-Tools\sd`; se
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

**Contra el código anterior.** `-p:RaizApp=` apunta el banco a otro árbol, así que se
lo corre contra un worktree en el commit previo al arreglo:

```bat
git worktree add ..\praia-baseline <commit-anterior> --detach
cd ..\praia-baseline
dotnet build -c Debug

cd <repo>\Tests\SeleccionDeModelos
dotnet run -p:EdicionApp=Debug -p:RaizApp=D:\PROYECTOS\PR-Tools\praia-baseline
```

**`RaizApp` tiene que ser una ruta absoluta.** MSBuild resuelve las relativas contra
la carpeta del banco, no contra el directorio actual, y un `HintPath` que no existe
no lo hace fallar: omite la referencia y el banco explota con decenas de «no se
encontró el tipo `PRImager`». Los `.csproj` verifican que el DLL esté y cortan con
ese motivo, pero conviene saberlo.

Contra `0a2ea1f` este banco da 7 OK y 5 fallas, y reproduce el síntoma que había
reportado el usuario. Después del arreglo, 13 y 0.

Terminado, se limpia el worktree:

```bat
git worktree remove ..\praia-baseline --force
```

**Mutando el código.** Se desactiva a propósito la barrera que el banco dice cubrir y
se comprueba que las aserciones fallan. Desactivar las dos barreras críticas del
borrado hace fallar 6 aserciones y borra el modelo activo de verdad. En
`GeneracionDeImagenes`, mutar las cuatro decisiones centrales hace fallar 7 aserciones
—y fue así como se descubrió que dos de ellas no probaban nada.

**Cuidado al revertir una mutación con `Copy-Item`.** Restaurar un respaldo devuelve
también su fecha de modificación, más vieja que el DLL que se compiló a partir del
código mutado. MSBuild ve el fuente «al día» y **no recompila**: el banco vuelve a
correr contra el binario mutado y uno cree estar mirando el código sano. Pasó al
escribir `GeneracionDeImagenes`. Después de revertir, tocar la fecha:

```powershell
Get-ChildItem Services\ImageGen\*.cs | ForEach-Object { $_.LastWriteTime = Get-Date }
```

## Al agregar un banco nuevo

`Tests\**` está excluido del `.csproj` de la aplicación. Sin esas exclusiones el SDK
tomaría estos `.cs` por los globs implícitos y los compilaría dentro del ejecutable
que se entrega —con dos `Main`, y nombrando tipos que en la edición Forense no deben
existir. Si se agrega una carpeta acá, no hace falta tocar nada; si se agrega un
banco fuera de `Tests\`, sí.
