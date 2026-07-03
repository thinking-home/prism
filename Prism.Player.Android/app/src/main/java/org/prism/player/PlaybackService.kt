package org.prism.player

import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService

// Фоновый сервис, который держит проигрыватель и «медиа-сессию».
// MediaSessionService — стандартная основа Media3: он сам показывает системное
// уведомление «сейчас играет» и даёт управлять воспроизведением снаружи
// (система/пульт, а позже — MQTT). Проигрыватель теперь живёт здесь, а не на экране.
class PlaybackService : MediaSessionService() {

    private var mediaSession: MediaSession? = null

    override fun onCreate() {
        super.onCreate()
        // Проигрыватель.
        val player = ExoPlayer.Builder(this).build()
        // Медиа-сессия — «витрина» проигрывателя для всей системы.
        mediaSession = MediaSession.Builder(this, player).build()
    }

    // Система/контроллеры спрашивают, какую сессию отдать при подключении. Отдаём нашу.
    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? =
        mediaSession

    override fun onDestroy() {
        // Освобождаем проигрыватель и сессию.
        val session = mediaSession
        if (session != null) {
            session.player.release()   // остановить и освободить проигрыватель
            session.release()          // освободить медиа-сессию
            mediaSession = null
        }
        super.onDestroy()
    }
}
