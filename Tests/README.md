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
borrado hace fallar 6 aserciones y borra el modelo activo de verdad.

## Al agregar un banco nuevo

`Tests\**` está excluido del `.csproj` de la aplicación. Sin esas exclusiones el SDK
tomaría estos `.cs` por los globs implícitos y los compilaría dentro del ejecutable
que se entrega —con dos `Main`, y nombrando tipos que en la edición Forense no deben
existir. Si se agrega una carpeta acá, no hace falta tocar nada; si se agrega un
banco fuera de `Tests\`, sí.
