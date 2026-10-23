# ms-notification (notification-service · :3006)

Centro de notificaciones de Cuida Tu Agua: bandeja por usuario y rol, preferencias por nivel de urgencia (HU-034), correo de alertas críticas (HU-027) y alerta de sensor desconectado (HU-032). Genera la alerta de válvula cerrada (HU-033) que dispara el correo.
.NET 10 · hexagonal · SQL Server (schema `notification`, lo crea **ms-notification-db**) · RabbitMQ · SMTP (Mailpit en desarrollo).

## Cómo funciona
```
device-service ──RabbitMQ──▶ notification.device-events ──▶ DeviceEventsHandler ──▶ NotificationDispatcher
   device.linked / unlinked                                                             │
   device.valve.reported            (válvula cerrada = CRÍTICA)                         ├─ app    → notifications (bandeja)
   device.reading.received          (prueba de vida)                                    └─ correo → email_outbox ──▶ EmailDeliveryWorker ──▶ SMTP
                                                                                              (dirección: ms-iam /internal/users/{id}/contact)
OfflineDeviceWorker (cada 15 s): dispositivo callado ≥ 1 min ──▶ alerta importante "Sensor desconectado" (una sola vez)
```

**Quién ve qué.** Una notificación es de UN usuario (`user_id`, contextual: "tu válvula se cerró") o de TODOS los de un rol (`role`, difusión). Cada persona ve las suyas más las de los roles de su token (`roles`: `USER`, `ADMIN`). La lectura es personal: que una la lea Juan no la marca leída para Ana. Pedir la de otra persona responde 404.

**Matriz de canales por nivel** (HU-034, valores por defecto; el usuario los cambia):

| Nivel | Se guarda como | App | Push | Correo | SMS/WhatsApp |
|---|---|---|---|---|---|
| Crítica | `CRITICAL` | sí (no se puede apagar) | sí | sí | sí |
| Importante | `WARNING` | sí | sí | no | no |
| Informativa | `INFO` | sí | no | no | no |

Se entregan **app**, **correo**, **push** y **SMS/WhatsApp** (HU-028, solo alertas **críticas**).

**Push (HU-026).** El teléfono registra su token de Expo (`POST /api/push-tokens`) y al cerrar sesión lo quita. Al llegar una alerta, si el usuario conserva el canal push de ese nivel, se envía a todos sus teléfonos por el servicio de Expo (`exp.host`), con `notificationId`, `type`, `severity` y `placeId` para que la app abra la pantalla correcta al tocarla. Es *best-effort*: si Expo no responde no se pierde nada (la alerta sigue en la app y el correo) y el evento no se reintenta; un evento repetido no vuelve a sonar el teléfono. Los tokens que Expo marca como `DeviceNotRegistered` (app desinstalada) se borran solos. "Entregado" significa *aceptado por Expo*; no se consultan los recibos finales. En Android hace falta una *development build* (Expo Go no recibe push remoto desde SDK 53).

**SMS / WhatsApp (HU-028).** Solo para alertas **críticas** de un usuario (nunca difusiones a un rol) y solo si el usuario conserva ese canal en sus preferencias. El mensaje se escribe en `sms_outbox` (misma cola con reintentos que el correo: 1 min, 5 min, 15 min, 1 h; a la 5.ª falla `FAILED`) y el `SmsDeliveryWorker` lo envía cada 10 s. El teléfono **no se guarda**: al enviar se pide a ms-iam el teléfono **verificado** (E.164, +57 Colombia / +593 Ecuador) y el **modo con que se verificó** (`SMS` o `WHATSAPP`); el mensaje sale por ese modo. Sin teléfono verificado queda `SKIPPED` (no se reintenta). Si el proveedor rechaza el número para siempre (no es móvil, no tiene WhatsApp…) queda `FAILED` sin reintentar. El texto es corto, con el lugar y el tipo de emergencia, y cabe en 1 SMS (160 caracteres GSM-7): para SMS se quitan las tildes (que obligarían a cobrar 2–3 SMS) y se conserva la ñ; WhatsApp lleva el texto con tildes. Proveedor según `Sms:Provider`: `twilio` (producción), `mailpit` (desarrollo: cada SMS es un correo a `sms.<número>@sms.cuidatuagua.local` o `whatsapp.<número>@…` en http://localhost:8025) o vacío (canal apagado).

**Código de verificación del teléfono.** ms-iam genera, guarda y valida el código; este servicio solo lo **entrega**, al instante y por el modo que eligió el usuario (`POST /internal/short-messages/verification-code`), así un número que no sirve para ese modo nunca se puede verificar con él. El código no se guarda aquí.

**Correo.** Se escribe en una cola (`email_outbox`) junto con la notificación, así que un SMTP caído no pierde la alerta: se reintenta a 1 min, 5 min, 15 min y 1 h; a la 5.ª falla queda `FAILED`. La dirección no se guarda: se pide a ms-iam al enviar. Un evento repetido no manda dos correos. Con `Smtp:Host` vacío el canal queda apagado.

**Sensor desconectado.** Solo para dispositivos que ya reportaron alguna vez (uno que nunca reportó se ve como "Nunca ha reportado", HU-013). Umbral `Devices:OfflineAfterMinutes` (10 por defecto). Una alerta hasta que el dispositivo reporte de nuevo.

## Endpoints (JWT de ms-iam)
| Método | Ruta | |
|---|---|---|
| GET | `/api/notifications?unreadOnly&before&limit` | mi bandeja, más reciente primero (30 por defecto, máx. 100) |
| GET | `/api/notifications/unread-count` | el número de la campana |
| POST | `/api/notifications/{id}/read` | marcar una como leída (idempotente) |
| POST | `/api/notifications/read-all` | marcar todas |
| GET | `/api/notification-preferences` | canales por nivel (INFO, WARNING, CRITICAL) |
| PUT | `/api/notification-preferences/{severity}` `{inApp,push,email,sms}` | 400 `preferences.critical_requires_in_app` si una crítica pierde la app · 400 `preferences.sms_only_critical` si se pide SMS/WhatsApp en `WARNING` o `INFO` (HU-028: solo críticas; al leer, esos niveles siempre traen `sms=false`) |
| POST | `/api/push-tokens` `{token,platform}` | `platform` = `android`/`ios`. 204 idempotente; 400 `push.invalid_token` / `push.invalid_platform` |
| DELETE | `/api/push-tokens` `{token}` | 204 siempre; solo borra si el token es del usuario |
| GET | `/health` | sin token |
| POST | `/internal/short-messages/verification-code` `{to,channel,code,validMinutes}` | **solo servicios** (`X-Internal-Key`, sin token; el gateway no la expone). HU-028: ms-iam envía el código que verifica un teléfono. 202 `{providerMessageId}` · 400 `sms.invalid_request` · 403 `auth.internal_only` · 422 `sms.rejected` (el número no sirve para ese modo) · 503 `sms.unavailable` / `sms.disabled` |

Errores en RFC 9457: el código estable va en `title` (`notification.not_found`, `preferences.critical_requires_in_app`, `auth.invalid_token`).

## Puesta en marcha
Antes: `ms-notification-db` aplicada (`docker compose up`, hasta `v1.4-device-liveness`), Redis y RabbitMQ arriba.
```powershell
cd C:\Users\User\Desktop\proyect\services\notification\backend\ms-notification
dotnet user-secrets set "ConnectionStrings:Notification" "Server=localhost,1433;Database=sy-water-db;User Id=notification_app;Password=<...>;TrustServerCertificate=True" --project src/SyWater.Notifications.Api
dotnet user-secrets set "RabbitMq:Password" "<RABBITMQ_PASSWORD>" --project src/SyWater.Notifications.Api
dotnet user-secrets set "Internal:ApiKey" "<la MISMA que en ms-devices>" --project src/SyWater.Notifications.Api
Copy-Item ..\..\..\iam\backend\iam-backend\keys\public.pem src\SyWater.Notifications.Api\keys\iam-public.pem -Force
dotnet test
dotnet run --project src/SyWater.Notifications.Api
```
En desarrollo el correo sale por Mailpit (`localhost:1025`, ver http://localhost:8025). El correo necesita que ms-iam exponga `GET /internal/users/{id}/contact` (`X-Internal-Key`, respuesta `{id,email,fullName}`); mientras no exista, los correos reintentan y terminan en `FAILED`.

## Configuración (publicación)
| Clave | Para qué | Ejemplo en producción |
|---|---|---|
| `Cors:AllowedOrigins` | orígenes del frontend web que pueden llamar a la API (sin esto el navegador bloquea) | `["https://app.cuidatuagua.com"]` |
| `App:PublicUrl` | base de los enlaces "Abrir en la app" de los correos | `https://app.cuidatuagua.com` |
| `Smtp:Host/Port/Security/Username/Password` | SMTP real (`Security`: `None`, `StartTls` 587, `Ssl` 465). La contraseña va en secretos, nunca en git | `smtp.proveedor.com`, `587`, `StartTls` |
| `Smtp:FromAddress` / `FromName` | remitente (debe estar autorizado en el proveedor) | `no-reply@cuidatuagua.com` |
| `Jwt:Issuer` + `keys/iam-public.pem` | los mismos que ms-iam | |
| `Redis:Configuration` | el mismo Redis de ms-iam (revocación de tokens) | |
| `Services:IamBaseUrl`, `Internal:ApiKey` | para pedir el correo y el teléfono verificado del usuario a ms-iam; la clave también protege `/internal/**` | |
| `Devices:OfflineAfterMinutes` | umbral de "sensor desconectado" | `10` |
| `RabbitMq:*` | broker; con `Host` vacío no consume eventos | |
| `Push:Enabled` | apaga el canal push (por defecto activo) | `true` |
| `Push:ExpoUrl` / `Push:ExpoAccessToken` | endpoint de Expo (por defecto el oficial) y token opcional si la cuenta de Expo activó seguridad reforzada. El token va en secretos, nunca en git | |
| `Sms:Provider` | HU-028: `twilio` · `mailpit` (desarrollo, necesita `Smtp:Host`) · vacío = SMS/WhatsApp apagado | `twilio` |
| `Twilio:AccountSid` / `Twilio:AuthToken` | cuenta de Twilio. El token va en secretos, nunca en git | |
| `Twilio:SmsFrom` / `Twilio:WhatsAppFrom` | número de Twilio que envía SMS y remitente de WhatsApp (E.164). Al menos uno | `+1…`, `+14155238886` (sandbox) |

## Pruebas
`dotnet test`: dominio, casos de uso con fakes, EF contra SQLite en memoria y la API completa con `WebApplicationFactory`. SQLite no reproduce algunos detalles de SQL Server (índices únicos con NULL, el `DENY` del rol): se validan al levantar con la BD real.
