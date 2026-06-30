# Network Player

Сетевой медиа-плеер. Сканирует папку с видеофайлами (например, двухчасовой `.mkv`)
и отдаёт каждый файл по **URL для потокового просмотра в браузере**. Если файл уже
в браузерном формате — он стримится напрямую; иначе, при наличии нужного кодека в
системе, файл обрабатывается через **ffmpeg** и выдаётся как **H264 или H265** по HLS.

## Как это устроено

```
браузер <──HTTP──> Host (ASP.NET Core / Kestrel)
                     ├── /                       страница-библиотека (список файлов)
                     ├── /watch/{id}             страница плеера (<video> + hls.js)
                     ├── /raw/{id}               прямой стрим с Range (браузерные файлы)
                     ├── /hls/{id}/playlist.m3u8 HLS-плейлист (VOD)
                     └── /hls/{id}/segment/N.ts  сегмент H264/H265, транскодируемый на лету
```

Для каждого файла плеер выбирает **режим воспроизведения** (`Media/MediaLibrary.cs`):

| Режим         | Когда                                                              | Доставка |
|---------------|-------------------------------------------------------------------|----------|
| `Direct`      | mp4/webm с браузерным кодеком (h264/vp9/av1 + aac/opus…)           | `/raw` с HTTP Range/перемоткой |
| `Transcode`   | любой другой контейнер/кодек **и** у ffmpeg есть декодер для него  | HLS, перекодирование в H264/H265 + AAC |
| `Unsupported` | нужна перекодировка, но ffmpeg или исходный кодек недоступны       | страница с пояснением |

### Стриминг и перемотка для двухчасового файла

HLS-плейлист — это **VOD-плейлист, рассчитанный заранее** по длительности файла
(`ffprobe`), поэтому браузер знает всю шкалу времени и может перематывать в любую
точку. Каждый 6-секундный сегмент `.ts` создаётся **по запросу**: ffmpeg быстро
перематывает (fast-seek) к нужному месту исходника, декодирует окно и перекодирует
его в целевой кодек, сдвигая таймстемпы сегмента на его глобальную позицию
(`-output_ts_offset`), чтобы плеер склеивал сегменты в один непрерывный поток.
Транскодируются только те сегменты, что находятся рядом с текущей позицией
воспроизведения.

## Требования

- .NET 10 SDK
- **ffmpeg + ffprobe** в `PATH` (или укажите `Player:FfmpegPath` / `Player:FfprobePath`).
  Без них напрямую отдаются только уже браузерные файлы.

## Установка ffmpeg

### Windows

Вариант через пакетный менеджер (рекомендуется):

```powershell
# winget (Windows 10/11)
winget install --id Gyan.FFmpeg -e

# либо Chocolatey
choco install ffmpeg

# либо Scoop
scoop install ffmpeg
```

Вручную: скачайте сборку с https://www.gyan.dev/ffmpeg/builds/ (архив
`ffmpeg-release-full`), распакуйте, например, в `C:\ffmpeg`, и добавьте
`C:\ffmpeg\bin` в переменную среды `PATH` (Параметры → Система → О системе →
Дополнительные параметры системы → Переменные среды). Проверка: `ffmpeg -version`.

### macOS

```bash
# Homebrew
brew install ffmpeg

# либо MacPorts
sudo port install ffmpeg
```

### Linux

```bash
# Debian / Ubuntu
sudo apt update && sudo apt install -y ffmpeg

# Fedora
sudo dnf install -y ffmpeg            # потребуется репозиторий RPM Fusion

# Arch / Manjaro
sudo pacman -S ffmpeg

# openSUSE
sudo zypper install ffmpeg

# универсально (Snap)
sudo snap install ffmpeg
```

Проверить установку на любой ОС: `ffmpeg -version` и `ffprobe -version`.

## Запуск

```bash
# положите видео в Host/videos/  (или укажите папку через --media)
dotnet run --project Host

# затем откройте http://localhost:8080
```

### Параметры командной строки

```bash
dotnet run --project Host -- --file "/путь/к/movie.mkv"    # отдать папку с этим файлом
dotnet run --project Host -- --media "/путь/к/библиотеке"  # отдать папку
dotnet run --project Host -- --codec h265                  # выдавать HEVC вместо H264
dotnet run --project Host -- --ffmpeg /opt/homebrew/bin/ffmpeg
```

## Конфигурация (`Host/appsettings.json`, секция `Player`)

| Ключ             | По умолчанию | Назначение |
|------------------|--------------|------------|
| `MediaDirectory` | `videos`     | папка, сканируемая на медиа (относительно приложения) |
| `FfmpegPath`     | *(авто)*     | явный путь к бинарю ffmpeg |
| `FfprobePath`    | *(авто)*     | явный путь к бинарю ffprobe |
| `OutputCodec`    | `h264`       | `h264` (libx264) или `h265` (libx265/HEVC) |
| `SegmentSeconds` | `6`          | длина HLS-сегмента |
| `EncoderPreset`  | `veryfast`   | пресет скорости x264/x265 |
| `Crf`            | `23`         | качество (меньше = лучше/больше) |

HTTP-эндпоинт задаётся в секции `Kestrel` (по умолчанию `http://0.0.0.0:8080`).

> Про H265: HEVC по HLS воспроизводится нативно в Safari, но не поддерживается
> hls.js в Chrome/Firefox, поэтому по умолчанию выбран `h264` для максимальной
> совместимости с браузерами.
