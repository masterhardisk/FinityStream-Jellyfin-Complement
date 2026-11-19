# FinityStream Notifications para Jellyfin

Recibe un aviso en FinityStream cuando tu servidor Jellyfin añade una película o un episodio a la biblioteca. El complemento es compatible con Jellyfin 10.11.x y necesita una API de FinityStream Notifications en funcionamiento.

## 1. Instala el complemento

1. Entra en Jellyfin como administrador y abre **Panel de control → Complementos → Repositorios**.
2. Añade un repositorio llamado **FinityStream Notifications** con esta URL:

   ```text
   https://github.com/masterhardisk/FinityStream-Jellyfin-Complement/releases/latest/download/manifest.json
   ```

3. Abre **Catálogo**, busca **FinityStream Notifications** e instálalo.
4. Reinicia Jellyfin.

El catálogo mostrará el complemento cuando GitHub Actions publique la primera versión.

## 2. Configura los avisos

Pide a quien gestione la API la URL de recepción de eventos y los dos secretos correspondientes a tu servidor Jellyfin. Si gestionas la API tú mismo, sigue las instrucciones de la sección siguiente para obtenerlos.

| Dato | Dónde se introduce | Para qué sirve |
| --- | --- | --- |
| URL del webhook | Jellyfin, campo **Webhook URL** | Indica a qué dirección debe enviar Jellyfin los eventos. Usa la URL HTTPS completa, incluida la ruta `/v1/webhooks/jellyfin`. |
| Secreto de webhook | Jellyfin, campo **Webhook secret** | Permite a la API autenticar los eventos de tu servidor. |
| Secreto de registro | App FinityStream | Autoriza el registro del dispositivo para recibir avisos de ese servidor. |

1. En Jellyfin, abre **Panel de control → Complementos → FinityStream Notifications**. Introduce la URL y el secreto, marca **Send notifications for new movies and episodes** y pulsa **Save**.
2. En una versión compatible de la app FinityStream, habilita los avisos para ese servidor y registra el dispositivo con el secreto de registro.

Si actualizas desde una versión que llevaba la URL incluida, introdúcela en **Webhook URL** y guarda. Si ya tienes un secreto guardado, dejar su campo vacío conserva el valor anterior.

### Si administras tu propia API

1. Abre la ruta `/System/Info/Public` de tu servidor Jellyfin y copia el valor de `Id`.
2. Desde la carpeta donde ejecutas Docker Compose para la API de notificaciones, ejecuta este comando con el ID que copiaste:

   ```sh
   docker compose exec notifications python -m app.provision ID_DE_JELLYFIN
   ```

3. El comando muestra **Webhook secret** y **Enrollment secret**. Usa el primero en Jellyfin y el segundo como secreto de registro en la app. Guarda ambos en privado. Cada servidor Jellyfin tiene sus propios secretos.
4. Forma la URL del webhook con el dominio HTTPS de tu API y la ruta `/v1/webhooks/jellyfin`.

## 3. Prueba una notificación

Con el dispositivo registrado y los avisos activados, añade una película o un episodio nuevo y espera a que Jellyfin lo incorpore a la biblioteca. El complemento envía ese evento a la API y la API entrega el aviso por APNs.

Si el aviso no llega, comprueba en este orden:

1. **Jellyfin.** Los avisos del complemento están activados y el servidor puede acceder a la URL HTTPS configurada. Revisa los registros de Jellyfin si aparece un error de envío.
2. **Credenciales.** Si Jellyfin registra un HTTP 401, verifica que el secreto de webhook corresponde al ID de ese servidor.
3. **App y API.** Comprueba que el dispositivo esté registrado para el mismo servidor y que la API use el entorno APNs de la app.

## Compilar y publicar

El complemento está escrito en C# y requiere el SDK de .NET 9 para compilarlo localmente:

```sh
dotnet build Jellyfin.Plugin.FinityStream.csproj -c Release
```

Para probar la compilación local, copia `bin/Release/net9.0/Jellyfin.Plugin.FinityStream.dll` a una subcarpeta del directorio de complementos y reinicia Jellyfin. En la imagen oficial de Docker, el directorio es `/config/plugins`.

GitHub Actions compila las pull requests. Cada push a `main` genera una versión a partir del número de ejecución, crea su tag y publica el ZIP y el manifiesto en GitHub Releases. Reejecutar el mismo workflow conserva la versión. La URL y las credenciales se configuran después de instalar el complemento.

### Cómo se entregan los avisos

El complemento escucha el evento `ItemAdded`, pone los avisos en una cola en memoria y reintenta los fallos transitorios. Reiniciar Jellyfin puede descartar eventos que sigan en esa cola. La API deduplica los eventos, guarda las entregas pendientes en MySQL y envía los avisos a los dispositivos registrados.
