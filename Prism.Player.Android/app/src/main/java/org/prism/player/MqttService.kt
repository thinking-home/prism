package org.prism.player

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.ComponentName
import android.content.Intent
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import org.json.JSONArray
import org.json.JSONObject

// Всегда-живая служба переднего плана: держит MQTT-клиент и «пульт» (MediaController)
// к службе-плееру PlaybackService. Своё уведомление она НИКОГДА не снимает — поэтому
// оно не пропадает. Она же публикует в MQTT статус плеера: info (статичная инфа) и
// state (что играет / пауза / позиция) — при изменениях и периодически.
class MqttService : Service() {

    private var mqtt: MqttController? = null
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var controller: MediaController? = null

    // Главный поток: на нём живёт MediaController (его нельзя трогать из чужих потоков).
    private val main = Handler(Looper.getMainLooper())
    // Периодическая публикация state — обновляет позицию и служит heartbeat'ом.
    private val stateTicker = object : Runnable {
        override fun run() {
            publishState()
            main.postDelayed(this, STATE_INTERVAL_MS)
        }
    }

    override fun onCreate() {
        super.onCreate()

        // «Пульт» к службе-плееру: командуем ей и читаем её состояние для state.
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        val future = MediaController.Builder(this, token).buildAsync()
        future.addListener({
            val c = future.get()
            controller = c
            // Публикуем state при изменениях воспроизведения (открыли/пауза/конец…).
            c.addListener(object : Player.Listener {
                override fun onPlaybackStateChanged(playbackState: Int) = publishState()
                override fun onIsPlayingChanged(isPlaying: Boolean) = publishState()
                override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) = publishState()
            })
            publishState()
        }, MoreExecutors.directExecutor())
        controllerFuture = future

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
            onConnected = { publishInfo(); main.post { publishState() } },
            onRefresh = { publishInfo(); publishState() },
        )
        mqtt?.start()
        main.post(stateTicker) // запускаем периодическую публикацию state
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Постоянное уведомление держит службу живой (иначе система её убьёт).
        startForeground(NOTIFICATION_ID, buildNotification())
        return START_STICKY // перезапустить, если система всё же убьёт
    }

    // Служба не привязывается (не bound) — работает сама по себе.
    override fun onBind(intent: Intent?): IBinder? = null

    // Не останавливаемся при закрытии приложения — MQTT должен слушать дальше.
    override fun onTaskRemoved(rootIntent: Intent?) {}

    // Открыть файл: играть присланный URL и вывести экран на передний план.
    private fun openFile(url: String) {
        val c = controller ?: return
        c.setMediaItem(MediaItem.fromUri(url))
        c.prepare()
        c.play()
        startActivity(
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }

    // Закрыть файл: убрать его — плеер снова «пустой».
    private fun closeFile() {
        controller?.clearMediaItems()
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
        val c = controller ?: return
        val state = JSONObject()
        val item = c.currentMediaItem
        if (item == null) {
            state.put("status", "idle")
        } else {
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

    override fun onDestroy() {
        main.removeCallbacks(stateTicker)
        mqtt?.stop()
        mqtt = null
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
        controller = null
        super.onDestroy()
    }

    // Простое постоянное уведомление службы.
    private fun buildNotification(): Notification {
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL_ID) == null) {
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Prism Player", NotificationManager.IMPORTANCE_LOW)
            )
        }
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Prism Player")
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setOngoing(true)
            .build()
    }

    companion object {
        private const val CHANNEL_ID = "prism_player_service"
        private const val NOTIFICATION_ID = 1
        private const val STATE_INTERVAL_MS = 5000L
    }
}
