package org.prism.player

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.ComponentName
import android.content.Intent
import android.os.IBinder
import androidx.media3.common.MediaItem
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import org.json.JSONArray
import org.json.JSONObject

// Всегда-живая служба переднего плана: держит MQTT-клиент и «пульт» (MediaController)
// к службе-плееру PlaybackService. Своё уведомление она НИКОГДА не снимает — поэтому
// оно не пропадает (в отличие от прошлой схемы, где им управляла ещё и Media3).
class MqttService : Service() {

    private var mqtt: MqttController? = null
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var controller: MediaController? = null

    override fun onCreate() {
        super.onCreate()

        // «Пульт» к службе-плееру, чтобы ей командовать (открыть/закрыть файл).
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        val future = MediaController.Builder(this, token).buildAsync()
        future.addListener({ controller = future.get() }, MoreExecutors.directExecutor())
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
            onConnected = { publishInfo() },
            onRefresh = { publishInfo() },
        )
        mqtt?.start()
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

    // Открыть файл: скомандовать плееру играть присланный URL и вывести экран вперёд.
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

    // Опубликовать статичную инфу о плеере (для обнаружения): имя + возможности.
    private fun publishInfo() {
        val name = Settings.playerName(this).ifEmpty { Settings.playerId(this) }
        val info = JSONObject()
            .put("name", name)
            .put("capabilities", JSONArray(listOf("open", "close", "refresh")))
        mqtt?.publish(Settings.infoTopic(this), info.toString(), retain = true)
    }

    override fun onDestroy() {
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
    }
}
