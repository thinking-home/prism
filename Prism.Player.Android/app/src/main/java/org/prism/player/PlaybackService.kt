package org.prism.player

import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService

// Фоновый сервис: держит проигрыватель + медиа-сессию и слушает MQTT-команды.
class PlaybackService : MediaSessionService() {

    private var mediaSession: MediaSession? = null
    private var mqtt: MqttController? = null

    override fun onCreate() {
        super.onCreate()
        val player = ExoPlayer.Builder(this).build()
        mediaSession = MediaSession.Builder(this, player).build()

        // Запускаем MQTT: команды open/close выполнятся ниже, над плеером.
        mqtt = MqttController(
            onOpen = { mediaId -> openFile(mediaId) },
            onClose = { closeFile() },
        )
        mqtt?.start()
    }

    // Открыть файл: загрузить поток Prism по id и начать играть.
    private fun openFile(mediaId: String) {
        val player = mediaSession?.player ?: return
        val url = "$PRISM_BASE/hls/$mediaId/playlist.m3u8"
        player.setMediaItem(MediaItem.fromUri(url))
        player.prepare()
        player.play()
    }

    // Закрыть файл: остановить и убрать его — плеер снова «пустой».
    private fun closeFile() {
        val player = mediaSession?.player ?: return
        player.stop()
        player.clearMediaItems()
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? =
        mediaSession

    override fun onDestroy() {
        mqtt?.stop()
        mqtt = null
        val session = mediaSession
        if (session != null) {
            session.player.release()
            session.release()
            mediaSession = null
        }
        super.onDestroy()
    }

    companion object {
        // Адрес Prism (10.0.2.2 = хост из эмулятора).
        private const val PRISM_BASE = "http://10.0.2.2:8080"
    }
}
