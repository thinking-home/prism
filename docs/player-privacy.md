---
 title: Privacy Policy — ThinkingHome Prism Player
---

# ThinkingHome Prism Player — Privacy Policy / Политика конфиденциальности

## English

Privacy Policy — ThinkingHome Prism Player
Last updated: October 24, 2026

Scope and contact
This policy covers ThinkingHome Prism Player for Android (ru.thinking_home.prism.player), published by Little Cow as part of the ThinkingHome ecosystem. Contact: dima117a@gmail.com. Project website: https://prism.thinking-home.ru/.

What the app does
The app plays media from URLs supplied by your controller through an MQTT broker that you configure. It does not require a developer-hosted account. The app has no advertising, analytics or automatic crash-reporting service integrated by the developer.

Data stored on your device
The app stores your MQTT broker address, optional username and password, player display name and a randomly generated player identifier in its private Android app storage. The identifier identifies this installation; it is not an advertising ID or hardware identifier. Credentials are not additionally encrypted by the app.
To resume playback, it stores media URLs, playback positions and save timestamps for up to 100 items. Older entries are removed as new ones are added. An entry is removed when a saved position is near the beginning or at least 95% through the media.

Network transfers and recipients
The configured broker receives your network IP address, player identifier and any credentials supplied for authentication. The app publishes the player name, supported commands, playback status, media URL and playback position/duration when available. These messages enable discovery and remote control. Status is published periodically, including while the app is in the background. MQTT information and status messages are retained by the broker and can be read by clients authorized to subscribe to their topics.
Media servers receive requests for media URLs, playlists, segments and related resources needed for playback, together with your IP address and normal network request information. URLs may contain access tokens or other private information. The recipients are the servers selected by you or your controller, not a mandatory developer-operated service. Their operators control their own logs and retention.
Android media sessions expose playback information and controls to the system and connected media controllers. The app does not sell data or send it to advertising networks.

Permissions and connection security
Internet access supports MQTT and media streaming. Foreground services, notifications and startup after device boot support playback and remote commands. Permission to display over other apps supports bringing the player to the screen for an incoming command; it is not used to capture other apps or screen content.
Encryption depends on your configuration. Plain HTTP and tcp:// MQTT connections are supported and are not encrypted. Use HTTPS and TLS-enabled MQTT (ssl://) on servers you trust; do not send sensitive credentials over unencrypted connections.

Retention, backups and deletion
Settings remain until changed or app data is cleared. Clear storage in Android Settings to remove local settings, credentials, the identifier and playback history. Uninstalling removes local app data. Android backup or device-transfer features may copy and later restore app data, depending on device and account settings; manage these copies through the system backup controls.
Clearing local data does not remove retained MQTT messages, server logs or backups. Ask the relevant server operator to remove those records. The developer cannot remotely erase data held on your device or your chosen servers.

Contacting us and changes
If you email us, your address and message are used to respond to the request and are processed by the email provider. Do not send passwords or private media URLs. You may request deletion of correspondence at the contact address; messages are retained only as needed to handle the request and any applicable obligations.
Updates to this policy will be posted on the project website with a revised date. This policy describes the Android app; visiting external websites or using external services is also subject to their own privacy practices.

## Русский

Политика конфиденциальности — ThinkingHome Prism Player
Обновлено: 24 октября 2026 года

Область действия и контакты
Политика относится к ThinkingHome Prism Player для Android (ru.thinking_home.prism.player), опубликованному разработчиком Little Cow в экосистеме ThinkingHome. Контакт: dima117a@gmail.com. Сайт проекта: https://prism.thinking-home.ru/.

Назначение приложения
Приложение воспроизводит медиа по URL, полученным от вашего контроллера через настроенный вами MQTT-брокер. Учётная запись на сервере разработчика не требуется. Разработчик не встроил в приложение рекламу, аналитику или автоматическую отправку отчётов о сбоях.

Данные на устройстве
В закрытом хранилище приложения Android сохраняются адрес MQTT-брокера, необязательные логин и пароль, имя плеера и случайно созданный идентификатор плеера. Он обозначает установку приложения и не является рекламным или аппаратным идентификатором. Приложение не применяет дополнительное шифрование сохранённых учётных данных.
Для продолжения просмотра сохраняются URL медиа, позиции воспроизведения и время сохранения — до 100 записей. При добавлении новых старые записи вытесняются. Запись удаляется при сохранении позиции у начала медиа или после просмотра не менее 95% его длительности.

Передача данных и получатели
Настроенный брокер получает сетевой IP-адрес, идентификатор плеера и указанные учётные данные для аутентификации. Приложение публикует имя плеера, поддерживаемые команды, состояние воспроизведения, URL медиа, позицию и длительность, когда они доступны. Это необходимо для обнаружения плеера и дистанционного управления. Статус передаётся периодически, в том числе при работе приложения в фоне. Информация и статус сохраняются брокером как retained-сообщения и доступны клиентам, которым разрешена подписка на соответствующие топики.
Медиасерверы получают запросы к URL медиа, плейлистам, сегментам и связанным ресурсам для воспроизведения, а также IP-адрес и обычные сведения сетевых запросов. URL могут содержать токены доступа и другую закрытую информацию. Получатели — серверы, выбранные вами или вашим контроллером; обязательного сервера разработчика нет. Операторы серверов самостоятельно управляют своими журналами и сроками хранения.
Медиасессия Android предоставляет сведения о воспроизведении и управление системе и подключённым медиаконтроллерам. Приложение не продаёт данные и не отправляет их рекламным сетям.

Разрешения и защита соединений
Доступ к интернету нужен для MQTT и загрузки медиа. Фоновые службы, уведомления и запуск после перезагрузки поддерживают воспроизведение и приём команд. Разрешение показа поверх других приложений используется для вывода плеера на экран по входящей команде, а не для записи экрана или содержимого других приложений.
Шифрование зависит от настроек. Поддерживаются незашифрованные HTTP и MQTT через tcp://. Используйте HTTPS и MQTT с TLS (ssl://) на доверенных серверах; не передавайте конфиденциальные учётные данные через незашифрованные соединения.

Хранение, резервные копии и удаление
Настройки хранятся до изменения или очистки данных приложения. Для удаления локальных настроек, пароля, идентификатора и истории позиций очистите хранилище приложения в настройках Android. Удаление приложения удаляет его локальные данные. Системные функции резервного копирования и переноса устройства могут копировать и затем восстанавливать данные приложения в зависимости от настроек устройства и аккаунта. Управлять такими копиями нужно через системные настройки резервного копирования.
Очистка локальных данных не удаляет retained-сообщения MQTT, журналы серверов и резервные копии. Для их удаления обратитесь к оператору соответствующего сервера. Разработчик не может дистанционно удалить данные с вашего устройства или выбранных вами серверов.

Обращения и изменения политики
Если вы отправите нам письмо, адрес и содержание используются для ответа и обрабатываются почтовым провайдером. Не присылайте пароли и закрытые URL медиа. Запросить удаление переписки можно по указанному email; сообщения хранятся лишь столько, сколько необходимо для обработки обращения и выполнения применимых обязательств.
Изменения политики публикуются на сайте проекта с новой датой. Эта политика описывает Android-приложение; посещение внешних сайтов и использование внешних сервисов также регулируется их собственными правилами конфиденциальности.
