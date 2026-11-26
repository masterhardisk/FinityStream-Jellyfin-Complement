# FinityStream Notifications para Jellyfin

Recibe un aviso en FinityStream cuando tu servidor Jellyfin añade una película o un episodio a la biblioteca. El complemento es compatible con Jellyfin 10.11.x.

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

Abre **Panel de control → Complementos → FinityStream Notifications**, activa los avisos y pulsa **Save**. El complemento se conecta automáticamente al servicio de FinityStream.

Puedes elegir si quieres avisos visibles por películas y episodios. Antes de enviar un aviso visible, el complemento espera a que Jellyfin reporte una imagen principal para el elemento (o para la serie en el caso de un episodio). Si Jellyfin no obtiene una imagen en diez minutos, el aviso se envía igualmente. La opción de agrupar episodios espera tres minutos desde el primer episodio y reúne las altas en un solo aviso.

Los avisos de películas y series incluyen el año de producción cuando Jellyfin lo proporciona, por ejemplo, `Blade Runner (1982)`. Si no hay año disponible, se muestra solo el título.

## 3. Prueba una notificación

Con el dispositivo registrado y los avisos activados, añade una película o un episodio nuevo y espera a que Jellyfin lo incorpore a la biblioteca. Deberías recibir un aviso en FinityStream.

Si el aviso no llega, comprueba en este orden:

1. **Jellyfin.** Los avisos del complemento están activados y el servidor puede acceder a la URL HTTPS configurada. Revisa los registros de Jellyfin si aparece un error de envío.
2. **Credenciales.** Si Jellyfin registra un HTTP 401, contacta con soporte de FinityStream.
3. **FinityStream.** Comprueba que los avisos estén activados para ese servidor en la app.

## Compilar y publicar

El complemento está escrito en C# y requiere el SDK de .NET 9 para compilarlo localmente:

```sh
dotnet build Jellyfin.Plugin.FinityStream.csproj -c Release
```

Para probar la compilación local, copia `bin/Release/net9.0/Jellyfin.Plugin.FinityStream.dll` a una subcarpeta del directorio de complementos y reinicia Jellyfin. En la imagen oficial de Docker, el directorio es `/config/plugins`.

GitHub Actions compila las pull requests. Cada push a `main` genera una versión a partir del número de ejecución, crea su tag y publica el ZIP y el manifiesto en GitHub Releases. Reejecutar el mismo workflow conserva la versión. La URL y las credenciales se configuran después de instalar el complemento.

### Cómo se entregan los avisos

El complemento escucha las altas, modificaciones y eliminaciones de la biblioteca, pone los avisos en una cola en memoria y reintenta los fallos transitorios. Reiniciar Jellyfin puede descartar eventos que sigan en esa cola.

Las peticiones POST, PUT, PATCH y DELETE correctas de usuarios autenticados también generan una actualización silenciosa para los dispositivos de su cuenta, incluidas las acciones de Jellyfin Enhanced. Se excluyen las rutas de registro y acceso de FinityStream para evitar avisos por registrar dispositivos. Requiere la migración `007_user_changes.sql` del servicio de notificaciones. Los clientes deben refrescar sus datos al recibir el push.
