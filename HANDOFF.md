# Контекст текущей работы (для переезда между машинами)

Рабочий файл на время п.14 «Библиотека — отдельный сервис» (`TODO.md`). Когда
пункт будет закрыт — удалить. **Статус и спека живут в `TODO.md`**, здесь только
то, чего там нет: договорённости, рецепты проверки и машинно-зависимые вещи.

Ветка: **`library`** (от `main`).

## Где остановились

Сделаны шаги 1–4 п.14 (подробности каждого — в `TODO.md`):

1. хост готов к опросу извне (кэш декодеров, персистентный кэш ffprobe, фоновая
   доразборка + `streamType:"pending"`, `relativePath` в DTO, ручка преемника);
2. сервис `Prism.Library` + `Prism.Library.Console`, `HttpMediaIdentity`,
   бухгалтерия «id → хост» (`Prism_FileHost`, миграция 4);
3. агрегированный каталог `/api/media` (слияние хостов, мета, абсолютные URL);
4. плееры: общий `Prism.Mqtt` (реестр + `BrokerOptions`), `/api/players`,
   `POST /api/players/{id}/open`; лаунчер переведён на общий реестр.

**Следующий — шаг 5** (веб-клиент на библиотеку). Предложенная нарезка:
API-слой клиента + бейдж `pending` → раздача статики библиотекой и same-origin
дефолт → удаление плагина `Prism.Plugins.Library` из хоста.

> Если шаг 4 ещё не закоммичен, в рабочем дереве должны быть: новый каталог
> `Prism.Mqtt/`, новый `Prism.Library/PlayerEndpoints.cs`, удалённый
> `Prism.Launcher/MqttBridge.cs`, правки `Prism.Launcher/*`,
> `Prism.Library/{Prism.Library.csproj,PrismLibraryApp.cs}`,
> `Prism.Library.Console/appsettings.json`, `Prism.sln`, `TODO.md`.

## Режим работы

- Маленькие шаги: один кусочек → сборка и живая проверка → ревью пользователя →
  **коммитит пользователь сам**. Не браться за следующий без «go».
- Делать только явно согласованное; «заодно» и «на вырост» — не делать.
- Не подавлять предупреждения компилятора о типах (`!`, pragma) без явного
  подтверждения — устранять перестройкой кода.
- Итог каждого шага дописывать в `TODO.md` (он источник истины по статусу).
- Память ассистента (`~/.claude/projects/…`) с репозиторием **не переезжает** —
  этот файл её заменяет.

## Решения, которые легко нарушить по незнанию

- **Не опрашивать все хосты ради отдельного файла** — ни при каких
  обстоятельствах. Преемник и карточка идут адресно через `FileHostLedger`;
  id без записей в бухгалтерии остаётся без ремапа (осознанно).
- «Хост недоступен» и «файла нет на диске» — **одно** состояние `present:false`,
  записи не удаляются.
- Плагин `Prism.Plugins.Library` **заморожен** (жив до шага 5) — правки вносим
  только в `Prism.Library`. Плагин и сервис **нельзя** подключать к одной БД:
  у сервиса миграция 4, у плагина максимум 3.
- Ключ миграций `prism.library` менять нельзя — по нему подхватывается
  существующая боевая БД.
- Режим воспроизведения (`direct`/`hls`/`unsupported`) зависит от окружения и
  не кэшируется; в `mediainfo.json` кэшируется только разбор ffprobe.

## Рецепты проверки

Порты: **8080** хост, **8081** библиотека, **8082** второй хост в тестах,
5173 веб-клиент (Vite), 5174 docs, 1883 MQTT.

```bash
# сборка решения целиком (2 предупреждения NU1903 — ожидаемы, см. ниже)
dotnet build Prism.sln
```

```bash
# хост (медиапапка добавляется к списку из appsettings)
dotnet run --project Prism.Host.Console -- --media /путь/к/видео
```

```bash
# второй хост для проверки агрегации и дедупа
Kestrel__Endpoints__Http__Url=http://0.0.0.0:8082 dotnet run --project Prism.Host.Console -- --media /tmp/prism-b-media
```

```bash
# библиотека: MQTT, второй хост и правила автозаполнения — через env, без правки конфига
Mqtt__Address=localhost Hosts__1__Name=second Hosts__1__BaseUrl=http://localhost:8082 Library__Rules__0__Path='subdir/{title}' Library__Rules__0__Node='Тест' Library__Rules__0__Meta__title='{title}' dotnet run --project Prism.Library.Console
```

```bash
# тестовый ролик (частота задаёт содержимое: другая частота = другой id)
ffmpeg -v error -f lavfi -i testsrc2=duration=2:size=320x240:rate=10 -f lavfi -i sine=frequency=440:duration=2 -c:v libx264 -c:a aac -shortest test.mkv -y
```

Проверка ремапа: перезаписать **тот же путь** роликом с другой частотой →
дёрнуть `/api/media` хоста (пересканирует) → `POST /api/library/scan`.

MQTT (брокер — контейнер `mosquitto` в podman, клиентов в PATH нет):

```bash
podman start mosquitto
```

```bash
podman exec mosquitto mosquitto_pub -h localhost -t prism/player/tv-test/info -r -m '{"name":"Тестовый ТВ"}'
```

```bash
podman exec mosquitto mosquitto_sub -h localhost -t 'prism/player/tv-test/cmd' -C 1 -W 25
```

```bash
podman exec mosquitto mosquitto_pub -h localhost -t prism/player/tv-test/info -r -n
```

Грабли MQTT-тестов: `online` считается по свежести `state` (окно 15 с) —
перед проверкой списка плееров опубликовать `state` заново. Retained-сообщения
тестового плеера после проверок **вычищать** (публикация пустого тела, как выше).

```bash
# остановить конкретный сервис (pkill по имени убьёт сразу все хосты)
kill $(lsof -nP -t -iTCP:8080 -sTCP:LISTEN)
```

```bash
# типы веб-клиента
cd Prism.Client && npx tsc -b --force
```

Данные (всё в `.gitignore`, переносить не нужно):
`Prism.Host.Console/data/{fingerprints.json,mediainfo.json}`,
`Prism.Library.Console/data/prism.db`, `logs/` у обоих.

## Что поправить после переезда

- `Prism.Host.Console/appsettings.json` → `Player:MediaDirectories` сейчас
  `/Users/dima117a/tmp-video` (машинный путь предыдущей машины).
- `Prism.Library.Console/appsettings.json` → `Hosts[0].BaseUrl` =
  `http://localhost:8080`, `Mqtt.Address` пустой (в тестах передавался через env).
- Нужны: .NET 10 SDK (проверялось на 10.0.301), ffmpeg + ffprobe в PATH, Node для
  веб-клиента, брокер MQTT для плееров (см. README).
- Windows-only и на macOS не проверяется: `Prism.Launcher` (только компиляция,
  `EnableWindowsTargeting`), `Prism.Host.Setup`, служба.

## Открытые хвосты

- **Лаунчер после перевода на `Prism.Mqtt` проверен только компиляцией** —
  прогнать «Отправить» → выбор плеера на Windows при случае.
- Веб-клиент не знает про `streamType:"pending"` и покажет такой файл как
  «недоступно» до обновления страницы — закрыть в шаге 5.
- `data/mediainfo.json` копится вечно (записи удалённых файлов не выпадают) —
  при желании привязать чистку к `gc`.
- NU1903 (`SQLitePCLRaw` через EF) — живём осознанно, кастомный резолв откатывали.
- п.13 (ложная привязка файлов дорожек по префиксу) и п.3а (вынос синтаксиса
  шаблонов в отдельный проект с тестами) ждут своей очереди после п.14.
