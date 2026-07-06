package org.prism.player

import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService

// Служба-плеер: держит проигрыватель и медиа-сессию. Media3 сама показывает медиа-
// уведомление (карточку управления) во время воспроизведения. Командами MQTT
// управляет отдельная служба MqttService (через MediaController) — здесь MQTT нет,
// чтобы не мешать управлению «передним планом» Media3.
class PlaybackService : MediaSessionService() {

    private var mediaSession: MediaSession? = null

    override fun onCreate() {
        super.onCreate()
        val player = ExoPlayer.Builder(this).build()
        mediaSession = MediaSession.Builder(this, player).build()
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? =
        mediaSession

    override fun onDestroy() {
        val session = mediaSession
        if (session != null) {
            session.player.release()
            session.release()
            mediaSession = null
        }
        super.onDestroy()
    }
}
