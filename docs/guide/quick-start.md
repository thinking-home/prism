# Быстрый старт

Запуск из исходников: сервер и веб-клиент. Для Windows есть готовый инсталлятор
службы — см. репозиторий, раздел «Служба Windows и инсталлятор».

## Что понадобится

- **.NET 10 SDK** — сервер;
- **Node.js 18+** и npm — веб-клиент;
- **ffmpeg и ffprobe** в `PATH` (или путь в настройках, или подпапка `ffmpeg/`
  рядом с приложением). Без них напрямую отдаются только браузерные файлы.

## Сервер

```bash
dotnet build Prism.sln

# положите видео в Prism.Host.Console/videos/ (или укажите папку через --media)
dotnet run --project Prism.Host.Console
# API слушает http://localhost:8080
```

Папки с медиа задаются в `appsettings.json` (`Player:MediaDirectories`) — их
может быть несколько. Ключи командной строки `--media` и `--file` **добавляются**
к списку из конфига, а не заменяют его.

## Библиотека

Каталог, дерево групп и веб-клиент отдаёт отдельный сервис. Хосты, которые он
опрашивает, перечислены в `Prism.Library.Console/appsettings.json` (секция
`Hosts`; по умолчанию там один — `http://localhost:8080`).

```bash
dotnet run --project Prism.Library.Console
# API и веб-клиент слушают http://localhost:8081
```

## Веб-клиент

```bash
cd Prism.Client
npm install
npm run build                                 # статика в dist/
cp -r dist ../Prism.Library.Console/wwwroot   # её и раздаёт библиотека
```

Откройте http://localhost:8081 — настраивать нечего: клиент обращается к тому
же адресу, с которого открыт. Для разработки клиента есть дев-сервер Vite:

```bash
cd Prism.Client && npm run dev    # http://localhost:5173
```

Он живёт на своём порту, поэтому адрес библиотеки задаётся шестерёнкой в шапке
(хранится в localStorage) — это единственная настройка клиента.

## Проверить, что всё живо

```bash
curl http://localhost:8080/api/info      # хост: ffmpeg, кодек, папки, число файлов
curl http://localhost:8081/api/media     # библиотека: каталог со всех хостов
```

В ответе хоста видно доступность ffmpeg, выбранный кодек, список папок и число
найденных файлов; в ответе библиотеки у каждой записи есть `host`, `hostUrl` и
абсолютный `streamUrl`.
