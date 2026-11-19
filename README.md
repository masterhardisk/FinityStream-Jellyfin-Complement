# FinityStream Notifications para Jellyfin

El complemento avisa a FinityStream cuando añades una película o un episodio a Jellyfin. Es compatible con Jellyfin 10.11.x y requiere acceso a una instancia de la API FinityStream Notifications.

## Instalar desde Jellyfin

1. Entra en Jellyfin con una cuenta de administrador y abre **Panel de control → Complementos → Repositorios**.
2. Añade un repositorio llamado **FinityStream Notifications**. Pega la [URL del manifiesto](https://github.com/masterhardisk/FinityStream-Jellyfin-Complement/releases/latest/download/manifest.json).
3. En **Catálogo**, busca **FinityStream Notifications**, selecciona **Instalar** y reinicia Jellyfin cuando te lo pida.

El catálogo estará disponible cuando se publique la primera versión en GitHub.

## Configurar las notificaciones

Para recibir avisos necesitas una versión de FinityStream compatible con el registro de dispositivos. Esta versión del complemento entrega los eventos a la API. El registro desde la app queda pendiente de verificar.

1. Pide a quien administre la API de FinityStream el **secreto de webhook** y el **secreto de registro** de tu servidor. Si administras tu propia API, consulta el apartado siguiente.
2. En Jellyfin, abre **Panel de control → Complementos → FinityStream Notifications**. Introduce el secreto de webhook. El destino de producción viene incorporado en el complemento. Activa los avisos y guarda.
3. En FinityStream, habilita los avisos para ese Jellyfin y registra el dispositivo mediante el secreto de registro. El complemento comienza a enviar eventos cuando se añade contenido nuevo.

El secreto de webhook se usa solo en Jellyfin. El de registro autoriza vincular dispositivos a ese servidor. Compártelos de forma privada. Si gestionas varios Jellyfin, cada uno tiene sus propias credenciales.

### Si administras la API

Consulta `/System/Info/Public` en tu Jellyfin y copia el campo `Id`. En el directorio del Compose de FinityStream Notifications, ejecuta:

```sh
docker compose exec notifications python -m app.provision ID_DE_JELLYFIN
```

El comando genera los dos secretos. El complemento utiliza el destino HTTPS definido al compilar la release.

### Comprobar que funciona

Añade una película o episodio nuevo después de registrar un dispositivo. Si el aviso no llega, comprueba que el complemento está habilitado, que Jellyfin alcanza la URL HTTPS y que la API está configurada con el entorno APNs de la app. Los registros de Jellyfin muestran errores del webhook. Un HTTP 401 indica que debes revisar el ID aprovisionado y el secreto de webhook.

## Cómo está hecho

Es un complemento en C# para .NET 9. Escucha el evento `ItemAdded` de Jellyfin y envía a la API el ID del servidor, el ID del elemento y su tipo. Los envíos salen en segundo plano desde una cola acotada en memoria. El complemento reintenta los fallos transitorios. Los eventos que aún estén en esa cola pueden perderse si reinicias Jellyfin.

La API deduplica los eventos, almacena las entregas pendientes en MySQL y envía el aviso por APNs a los dispositivos inscritos en ese servidor. La clave de APNs se configura en la API.

## Compilar y distribuir

Con el SDK .NET 9, ejecuta desde la raíz del complemento:

```sh
export FINITYSTREAM_WEBHOOK_URL='https://TU_DOMINIO/ruta/del/webhook'
dotnet build Jellyfin.Plugin.FinityStream.csproj -c Release
```

En **Settings → Secrets and variables → Actions → Variables**, crea `FINITYSTREAM_WEBHOOK_URL` con la URL HTTPS completa del webhook. La URL se incorpora al DLL y es pública. Cada secreto de webhook se configura por servidor en Jellyfin.

El workflow de GitHub Actions compila las pull requests. Al subir o integrar cambios en `main`, calcula la versión con el número de ejecución. Después de compilar, crea el tag para ese commit y publica el ZIP y el manifiesto en GitHub Releases. Reejecutar el mismo workflow conserva la versión. La URL que se añade al catálogo es:

```text
https://github.com/masterhardisk/FinityStream-Jellyfin-Complement/releases/latest/download/manifest.json
```

Para una prueba local, también puedes copiar `bin/Release/net9.0/Jellyfin.Plugin.FinityStream.dll` a una subcarpeta propia dentro del directorio de complementos de Jellyfin y reiniciar el servidor. En la imagen oficial de Docker, ese directorio es `/config/plugins`.
