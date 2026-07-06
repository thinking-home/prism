# Prism.Player.Android

Нативный плеер под **Android TV** — «исполнитель» (renderer) в архитектуре Prism.
Играет видео из сервера **Prism** (HLS), управляется локально пультом через
**MediaSession** и внешне — по **MQTT** (универсальный API управления, туда же
репортит состояние). Привязан только к хосту Prism (контент); самодостаточен через
пульт Android TV; координация (напр. ThinkingHome) — снаружи, по MQTT.

## Роли в системе

- **Prism.Host** — источник: стриминг (HLS), библиотека и метаданные. Про
  воспроизведение не знает.
- **Prism.Player.Android** (этот проект) — рендерер: декодирует и показывает,
  держит состояние play/pause/позиция, принимает команды.
- **Координатор** (ThinkingHome и др.) — снаружи, шлёт команды и читает состояние
  по MQTT. Своего протокола нет — только стандартный MQTT + этот контракт.

## Стек

- **Kotlin**, **Media3/ExoPlayer** (HLS, дорожки, субтитры, скорость);
- **`PlaybackService`** (`MediaSessionService`) — проигрыватель + медиа-сессия;
  Media3 сама показывает медиа-уведомление (управление пультом/CEC/ассистентом);
- **`MqttService`** — всегда-живая служба переднего плана: MQTT-клиент + «пульт»
  (`MediaController`) к плееру; своё постоянное уведомление;
- **Prism API-клиент** (Retrofit/Ktor) — `/api/media`, URL потока, субтитры;
- **UI на Compose for TV** — библиотека + плеер, навигация D-pad;
- лаунчер-иконка (`LEANBACK_LAUNCHER` + banner), deep-link `prism://play/{id}`.

## Конфигурация

- URL хоста **Prism** (единственная привязка контента);
- адрес брокера **MQTT** (+ логин/пароль при необходимости);
- **идентификатор плеера** `{id}` и отображаемое имя (напр. `living-room` / «Гостиная»).

## MQTT-контракт управления

Префикс `prism`, `{id}` — идентификатор плеера. Пейлоады — JSON (UTF-8).

### Плеер → брокер

| Топик | Назначение | Retain |
|-------|-----------|:------:|
| `prism/player/{id}/availability` | `"online"` / `"offline"` (LWT = `offline`) | да |
| `prism/player/{id}/info` | статичная инфа о плеере | да |
| `prism/player/{id}/state` | текущее состояние (при изменениях + позиция) | да |
| `prism/player/{id}/event` | разовые события | нет |

Поля `info`: `name` — отображаемое имя; **`prismUrl` — адрес хоста Prism, с которым
настроен работать этот плеер** (координатор берёт отсюда, к какому серверу резолвить
`mediaId`); `capabilities` — что поддерживает плеер.

```jsonc
// info
{"name":"Гостиная","prismUrl":"http://prism:8080","capabilities":["audio","subtitle","rate"]}

// state
{"status":"playing",           // idle | buffering | playing | paused | ended
 "mediaId":"abc123","title":"Патриот · S01E02",
 "positionSec":123.4,"durationSec":2555.1,
 "audio":0,"subtitle":null,"rate":1.0}

// event
{"type":"ended","mediaId":"abc123"}
{"type":"error","message":"..."}
```

### Брокер → плеер (команды публикует координатор)

Топик `prism/player/{id}/cmd`, пейлоад — JSON:

```jsonc
{"action":"play","mediaId":"abc123","positionSec":0,"audio":0,"subtitle":null}
{"action":"pause"}
{"action":"resume"}
{"action":"stop"}
{"action":"seek","positionSec":120}
{"action":"next"}                      // следующая серия
{"action":"previous"}                  // предыдущая серия
{"action":"setAudio","index":1}
{"action":"setSubtitle","index":0}     // отсутствует/null = выкл
{"action":"setRate","rate":2.0}
```

- `state` и `availability` — **retained**: поздно подключившийся координатор сразу
  видит, кто онлайн и что играет.
- Обнаружение плееров — подписка на `prism/player/+/info` и `prism/player/+/state`.
- Команды MQTT и локальный пульт/ассистент идут в **один и тот же** внутренний
  контроллер плеера — единый путь управления.

### Поток «включи фильм X»

1. Координатор (TH) резолвит название → `mediaId` через HTTP Prism (`/api/media`,
   метаданные библиотеки).
2. Публикует `{"action":"play","mediaId":"…"}` в `prism/player/{id}/cmd`.
3. Плеер тянет поток из Prism и играет, обновляя `state`.

Команда `next`/`previous` — «следующая/предыдущая серия»: плеер знает сериал/сезон/
эпизод текущего файла из метаданных Prism и вычисляет соседний эпизод сам.

## Фон, автозапуск и активация (реализовано)

Чтобы плеер принимал MQTT-команды даже когда приложение закрыто, и выводил видео на
экран по команде — **две отдельные службы** (важно, чтобы они не мешали друг другу
управлять «передним планом»):

- **`MqttService`** — всегда-живая служба переднего плана: держит MQTT-клиент и «пульт»
  (`MediaController`) к плееру. Показывает **своё постоянное уведомление** (которым
  никто, кроме неё, не управляет — поэтому оно не пропадает), возвращает `START_STICKY`
  и переопределяет `onTaskRemoved`, чтобы **не останавливаться при закрытии приложения**.
- **`PlaybackService`** (`MediaSessionService`) — проигрыватель + медиа-сессия. Media3
  сама показывает медиа-уведомление во время воспроизведения и убирает после остановки.
- **Автозапуск по загрузке**: `BootReceiver` ловит `BOOT_COMPLETED` и запускает
  `MqttService`. Плюс `MqttService` стартует при открытии приложения (`MainActivity`).
- **Активация экрана по команде**: при `open`, если приложение свёрнуто, `MqttService`
  выводит экран на передний план (`startActivity`). Android ограничивает запуск экрана
  из фона (**Background Activity Launch**), поэтому нужно разрешение
  **`SYSTEM_ALERT_WINDOW`** («поверх других приложений») — пользователь выдаёт его один
  раз (на эмуляторе: `adb shell appops set org.prism.player SYSTEM_ALERT_WINDOW allow`).

Разрешения (манифест): `RECEIVE_BOOT_COMPLETED`, `SYSTEM_ALERT_WINDOW`,
`FOREGROUND_SERVICE` (+ `…_MEDIA_PLAYBACK`, `…_DATA_SYNC`), `POST_NOTIFICATIONS`.

Во время воспроизведения видны **два уведомления** — постоянное от `MqttService` и
медиа-управление от Media3; так и задумано (на ТВ они в области уведомлений, не мешают).

## Сборка

Требуется Android Studio / Android SDK (JDK, `adb`, Gradle). Сборка из терминала:

```bash
./gradlew assembleDebug        # собрать APK
./gradlew installDebug         # установить на подключённое устройство/эмулятор
```
