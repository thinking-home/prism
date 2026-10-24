package org.prism.player

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Handler
import android.os.Build
import android.os.IBinder
import android.os.Looper
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import org.json.JSONArray
import org.json.JSONObject

// User-enabled connected-device service for remote control over MQTT.
// Keeps a visible notification with Stop and publishes player info/state.
class MqttService : Service() {

    private var mqtt: MqttController? = null
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var controller: MediaController? = null
    private var pendingUrl: String? = null
    private var generation = 0
    private var active = false

    // Главный поток: на нём живёт MediaController (его нельзя трогать из чужих потоков).
    private val main = Handler(Looper.getMainLooper())
    // Периодическая публикация state — обновляет позицию и служит heartbeat'ом.
    private val stateTicker = object : Runnable {
        override fun run() {
            if (!active) return
            publishState()
            main.postDelayed(this, STATE_INTERVAL_MS)
        }
    }

    private fun connect() {
        active = true
        MqttStatus.setRunning(true)
        val currentGeneration = ++generation
        // «Пульт» к службе-плееру: командуем ей и читаем её состояние для state.
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        val future = MediaController.Builder(this, token).buildAsync()
        controllerFuture = future
        future.addListener({
            if (!active || generation != currentGeneration) return@addListener
            val c = try { future.get() } catch (_: Exception) {
                stopSelf()
                return@addListener
            }
            controller = c
            // Публикуем state при изменениях воспроизведения (открыли/пауза/конец…).
            c.addListener(object : Player.Listener {
                override fun onPlaybackStateChanged(playbackState: Int) = publishState()
                override fun onIsPlayingChanged(isPlaying: Boolean) = publishState()
                override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) = publishState()
            })
            publishState()
            playPendingIfVisible()
        }, MoreExecutors.directExecutor())

        // MQTT: адрес брокера, id, топик и логин/пароль берём из настроек.
        // Команды выполняем над плеером через controller (уже в главном потоке).
        mqtt = MqttController(
            brokerUrl = Settings.brokerUrl(this),
            clientId = Settings.clientId(this),
            cmdTopic = Settings.cmdTopic(this),
            mqttUser = Settings.mqttUser(this),
            mqttPassword = Settings.mqttPassword(this),
            onOpen = { url -> openFile(url) },
            onClose = { closeFile() },
            // При (пере)подключении и по refresh публикуем и info, и state.
            onConnected = { publishInfo(); publishState() },
            onRefresh = { publishInfo(); publishState() },
            dispatch = { callback -> main.post { callback() } },
            onConnectionChange = { connected ->
                MqttStatus.set(connected)
                updateNotification()
            },
        )
        mqtt?.start()
        main.post(stateTicker) // запускаем периодическую публикацию state
        updateNotification()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            Settings.setRemoteEnabled(this, false)
            disconnect()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        // Promote before media-session binding or any network work.
        try {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, buildNotification(),
                if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE else 0)
        } catch (_: IllegalStateException) {
            stopSelf()
            return START_NOT_STICKY
        } catch (_: SecurityException) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (!Settings.canRunRemote(this)) {
            disconnect()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        if (intent?.action == ACTION_RELOAD) disconnect()
        if (!active) connect()
        if (intent?.action == ACTION_VISIBLE) playPendingIfVisible()
        return START_STICKY
    }

    // Служба не привязывается (не bound) — работает сама по себе.
    override fun onBind(intent: Intent?): IBinder? = null

    // Не останавливаемся при закрытии приложения — MQTT должен слушать дальше.
    override fun onTaskRemoved(rootIntent: Intent?) {}

    // Открыть файл: играть присланный URL и вывести экран на передний план.
    // Если для этого URL сохранена позиция — продолжаем с неё (тост подсказывает).
    private fun openFile(url: String) {
        if (!active) return
        pendingUrl = url
        updateNotification()
        if (MainActivity.isVisible) {
            playPendingIfVisible()
        } else if (android.provider.Settings.canDrawOverlays(this)) {
            // Do not start mediaPlayback from the background. Wait for the activity
            // to become visible, even when an overlay grant permits its launch.
            try {
                startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            } catch (_: SecurityException) {
                // The notification remains available for an explicit user tap.
            }
        }
    }

    private fun playPendingIfVisible() {
        if (!active || !MainActivity.isVisible) return
        val url = pendingUrl ?: return
        val c = controller ?: return
        pendingUrl = null
        updateNotification()
        val resumeMs = PlaybackPositions.get(this, url)
        if (resumeMs != null) {
            c.setMediaItem(MediaItem.fromUri(url), resumeMs)
            Toast.makeText(this, getString(R.string.resume_from, formatTime(resumeMs)), Toast.LENGTH_LONG).show()
        } else {
            c.setMediaItem(MediaItem.fromUri(url))
        }
        c.prepare()
        c.play()
    }

    // Закрыть файл: убрать его — плеер снова «пустой». Позицию запоминаем до
    // очистки: после clearMediaItems её уже не прочитать.
    private fun closeFile() {
        pendingUrl = null
        updateNotification()
        val c = controller ?: return
        savePosition(c)
        c.clearMediaItems()
    }

    // Запоминает позицию текущего файла (для «продолжить с места»). В конце фильма
    // запись, наоборот, удаляется — пересматривать начнём с начала.
    private fun savePosition(c: MediaController) {
        val url = c.currentMediaItem?.localConfiguration?.uri?.toString() ?: return
        if (c.playbackState == Player.STATE_ENDED) {
            PlaybackPositions.clear(this, url)
            return
        }
        val dur = c.duration
        PlaybackPositions.save(this, url, c.currentPosition, if (dur == C.TIME_UNSET) 0 else dur)
    }

    // «1:23:45» или «7:05» — для тоста о продолжении.
    private fun formatTime(ms: Long): String {
        val s = ms / 1000
        return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, (s % 3600) / 60, s % 60)
        else "%d:%02d".format(s / 60, s % 60)
    }

    // Статичная инфа о плеере (для обнаружения): имя + возможности.
    private fun publishInfo() {
        val name = Settings.playerName(this).ifEmpty { Settings.playerId(this) }
        val info = JSONObject()
            .put("name", name)
            .put("capabilities", JSONArray(listOf("open", "close", "refresh")))
        mqtt?.publish(Settings.infoTopic(this), info.toString(), retain = true)
    }

    // Текущее состояние воспроизведения: что играет, пауза, позиция. Плеер читаем в
    // главном потоке; сама отправка уходит в фон внутри publish().
    private fun publishState() {
        if (!active) return
        val c = controller ?: return
        val state = JSONObject()
        val item = c.currentMediaItem
        if (item == null) {
            state.put("status", "idle")
        } else {
            // Попутно (тикер + события пауз/окончания) запоминаем позицию просмотра.
            savePosition(c)
            state.put("status", statusOf(c))
            item.localConfiguration?.uri?.let { state.put("url", it.toString()) }
            state.put("positionSec", c.currentPosition / 1000.0)
            val dur = c.duration
            if (dur != C.TIME_UNSET && dur > 0) state.put("durationSec", dur / 1000.0)
        }
        mqtt?.publish(Settings.stateTopic(this), state.toString(), retain = true)
    }

    // Строка статуса по состоянию ExoPlayer.
    private fun statusOf(c: MediaController): String = when {
        c.playbackState == Player.STATE_BUFFERING -> "buffering"
        c.playbackState == Player.STATE_ENDED -> "ended"
        c.playbackState == Player.STATE_IDLE -> "idle"
        c.isPlaying -> "playing"
        else -> "paused"
    }

    private fun disconnect() {
        active = false
        generation++
        pendingUrl = null
        main.removeCallbacks(stateTicker)
        controller?.let { savePosition(it); it.pause() }
        mqtt?.stop()
        mqtt = null
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
        controller = null
        MqttStatus.setRunning(false)
    }

    override fun onDestroy() {
        disconnect()
        super.onDestroy()
    }

    private fun updateNotification() {
        if (!active) return
        try {
            getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, buildNotification())
        } catch (_: SecurityException) {
            // Android 13+: notifications may be denied; settings still offer Stop.
        }
    }

    // Простое постоянное уведомление службы.
    private fun buildNotification(): Notification {
        val manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            manager.getNotificationChannel(CHANNEL_ID) == null) {
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "ThinkingHome Prism Player", NotificationManager.IMPORTANCE_LOW)
            )
        }
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("ThinkingHome Prism Player")
            .setContentText(getString(when {
                pendingUrl != null -> R.string.remote_pending
                MqttStatus.connected -> R.string.remote_listening
                else -> R.string.remote_connecting
            }))
            .setContentIntent(PendingIntent.getActivity(this, 0,
                Intent(this, MainActivity::class.java), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
            .addAction(android.R.drawable.ic_media_pause, getString(R.string.remote_stop),
                PendingIntent.getService(this, 1, Intent(this, MqttService::class.java).setAction(ACTION_STOP),
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setOnlyAlertOnce(true)
            .setOngoing(true)
            .build()
    }

    companion object {
        const val ACTION_STOP = "org.prism.player.STOP_REMOTE"
        const val ACTION_RELOAD = "org.prism.player.RELOAD_REMOTE"
        const val ACTION_VISIBLE = "org.prism.player.PLAYER_VISIBLE"

        fun startIfEnabled(context: Context, action: String? = null): Boolean {
            if (!Settings.canRunRemote(context)) return false
            return try {
                ContextCompat.startForegroundService(context, Intent(context, MqttService::class.java).setAction(action))
                true
            } catch (_: IllegalStateException) {
                MqttStatus.setRunning(false)
                false // Includes ForegroundServiceStartNotAllowedException on API 31+.
            } catch (_: SecurityException) {
                MqttStatus.setRunning(false)
                false
            }
        }
        private const val CHANNEL_ID = "prism_player_service"
        private const val NOTIFICATION_ID = 1
        private const val STATE_INTERVAL_MS = 5000L
    }
}
