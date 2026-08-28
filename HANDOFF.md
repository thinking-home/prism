# Контекст текущей работы (для переезда между машинами)

Рабочий файл на время п.14 «Библиотека — отдельный сервис» (`TODO.md`). Когда
пункт будет закрыт — удалить. **Статус и спека живут в `TODO.md`**, здесь только
то, чего там нет: договорённости, рецепты проверки и машинно-зависимые вещи.

Ветка: **`library`** (от `main`).

## Где остановились

Сделаны шаги 1–5 п.14 (подробности каждого — в `TODO.md`):

1. хост готов к опросу извне (кэш декодеров, персистентный кэш ffprobe, фоновая
   доразборка + `streamType:"pending"`, `relativePath` в DTO, ручка преемника);
2. сервис `Prism.Library` + `Prism.Library.Console`, `HttpMediaIdentity`,
   бухгалтерия «id → хост» (`Prism_FileHost`, миграция 4);
3. агрегированный каталог `/api/media` (слияние хостов, мета, абсолютные URL);
4. плееры: общий `Prism.Mqtt` (реестр + `BrokerOptions`), `/api/players`,
   `POST /api/players/{id}/open`; лаунчер переведён на общий реестр;
5. веб-клиент работает с библиотекой (ручки хоста — только со страницы фильма,
   по `hostUrl` записи), статику раздаёт библиотека (`Client:Path`, дефолт
   `wwwroot`), дефолт адреса в клиенте — same-origin; плагин
   `Prism.Plugins.Library` удалён из репозитория, решения, конфигов и MSI.

**Следующий — шаг 6** (инсталлятор `Prism.Library.Setup`; в MSI библиотеки едет
npm-сборка веб-клиента, поэтому сборке инсталлятора понадобится Node).

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
- Ключ миграций `prism.library` менять нельзя — по нему подхватывается
  существующая боевая БД.
- Режим воспроизведения (`direct`/`hls`/`unsupported`) зависит от окружения и
  не кэшируется; в `mediainfo.json` кэшируется только разбор ffprobe.

## Рецепты проверки

Порты: **8080** хост, **8081** библиотека, **8082** второй хост в тестах,
5173 веб-клиент (Vite), 5174 docs, 1883 MQTT. На этой машине 8080 занят
установленной службой `PrismHost`, поэтому хост из исходников поднимаем на 8082.

```bash
# сборка решения целиком (предупреждений быть не должно)
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

ffmpeg и ffprobe **в PATH этой машины отсутствуют**: хост из исходников
поднимется, но все файлы покажет `unsupported`. Бинарники есть внутри установки
(их кладёт MSI) — путь передаётся переменными окружения:

```bash
Player__FfmpegPath='C:\Program Files\Prism Hostfmpegfmpeg.exe' Player__FfprobePath='C:\Program Files\Prism Hostfmpegfprobe.exe' dotnet run --project Prism.Host.Console -- --media /путь/к/видео
```

```bash
# библиотека: MQTT, второй хост и правила автозаполнения — через env, без правки конфига
Mqtt__Address=localhost Hosts__1__Name=second Hosts__1__BaseUrl=http://localhost:8082 Library__Rules__0__Path='subdir/{title}' Library__Rules__0__Node='Тест' Library__Rules__0__Meta__title='{title}' dotnet run --project Prism.Library.Console
```

```bash
# тестовый ролик (частота задаёт содержимое: другая частота = другой id)
"/c/Program Files/Prism Host/ffmpeg/ffmpeg.exe" -v error -f lavfi -i testsrc2=duration=2:size=320x240:rate=10 -f lavfi -i sine=frequency=440:duration=2 -c:v libx264 -c:a aac -shortest test.mkv -y
```

Проверка ремапа: перезаписать **тот же путь** роликом с другой частотой →
дёрнуть `/api/media` хоста (пересканирует) → `POST /api/library/scan`.

MQTT: брокер — **служба Windows `mosquitto` 2.1.2**, конфиг
`C:\Program Files\mosquitto\mosquitto.conf`, в нём всего две активные строки:
`listener 1883` и `allow_anonymous true`. Без них 2.x слушает только loopback и
приставка до брокера не достучится (ровно на это напоролись 2026-08-28); наружу
порт пускает правило брандмауэра «Mosquitto MQTT (LAN)». Клиенты лежат рядом с
брокером, в PATH их нет:

```powershell
& 'C:\Program Files\mosquitto\mosquitto_pub.exe' -h localhost -t prism/player/tv-test/info -r -m '{"name":"Тестовый ТВ"}'
```

```powershell
& 'C:\Program Files\mosquitto\mosquitto_sub.exe' -h localhost -t 'prism/player/+/cmd' -v -W 25
```

```powershell
& 'C:\Program Files\mosquitto\mosquitto_pub.exe' -h localhost -t prism/player/tv-test/info -r -n
```

Грабли MQTT-тестов: `online` считается по свежести `state` (окно 15 с) —
перед проверкой списка плееров опубликовать `state` заново. Retained-сообщения
тестового плеера после проверок **вычищать** (публикация пустого тела, как выше).

```powershell
# остановить сервис, занявший порт (по номеру порта, а не по имени процесса)
Stop-Process -Id (Get-NetTCPConnection -LocalPort 8081 -State Listen).OwningProcess
```

```bash
# типы веб-клиента
cd Prism.Client && npx tsc -b --force
```

```bash
# статику библиотека берёт из своей папки wwwroot (в .gitignore)
cd Prism.Client && npm run build && cp -r dist ../Prism.Library.Console/wwwroot
```

Данные (всё в `.gitignore`, переносить не нужно):
`Prism.Host.Console/data/{fingerprints.json,mediainfo.json}`,
`Prism.Library.Console/data/prism.db`, `logs/` у обоих.

## Эта машина (Windows 10; переезд с macOS сделан 2026-08-27)

- Инструменты: .NET SDK 10.0.400, Node v24.19.0. **ffmpeg/ffprobe в PATH нет** —
  они есть только внутри установки Prism Host (как их подсунуть — в рецептах).
- Из MSI установлены служба `PrismHost` (порт 8080, медиапапка
  `F:\Little Cow\Video`) и лаунчер в подпапке `launcher/`; правило брандмауэра
  «Prism Host» создаёт сам инсталлятор. У лаунчера `Host.Address` и
  `Broker.Address` = `192.168.1.96`: **`localhost` там смертелен** — этот адрес
  уезжает в MQTT-команду, и приставка пытается открыть поток у самой себя
  (на это напоролись 2026-08-28).
- Брокер mosquitto — служба Windows, настроен на приём из сети (см. рецепты).
- Лаунчер после перевода на `Prism.Mqtt` **проверен вживую здесь** (2026-08-28):
  «Отправить» → команда доехала до приставки, фильм играет. Тогда же у него
  появилась пометка на значке трея, когда нет подключения к брокеру, а MSI
  научился закрывать лаунчер при удалении и обновлении (`util:CloseApplication`).
- `Prism.Host.Console/appsettings.json` в репозитории всё ещё содержит
  macOS-путь `/Users/dima117a/tmp-video` (Windows разворачивает его в
  `D:\Users\dima117a\tmp-video`, такой папки нет) — при запуске из исходников
  папку задавать ключом `--media`.
- `Prism.Library.Console/appsettings.json` → `Hosts[0].BaseUrl` =
  `http://localhost:8080`, `Mqtt.Address` пустой (в тестах передаётся через env).
- Переустановка MSI: версия пакета **0.3.0 не встаёт поверх самой себя**
  (`MajorUpgrade` равные версии не обновляет) — сначала удалить старую. Удаление
  затирает `C:\Program Files\Prism Host\appsettings.json` (п.4 TODO), поэтому
  медиапапки сохранять заранее.
- Не проверяется на этой машине: сборка и запуск под macOS/Linux (раньше было
  наоборот — Windows-часть шла только компиляцией).

## Открытые хвосты

- `data/mediainfo.json` копится вечно (записи удалённых файлов не выпадают) —
  при желании привязать чистку к `gc`.
- п.13 (ложная привязка файлов дорожек по префиксу) и п.3а (вынос синтаксиса
  шаблонов в отдельный проект с тестами) ждут своей очереди после п.14.
