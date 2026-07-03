package org.prism.player

import android.app.Activity
import android.content.ComponentName
import androidx.media3.common.MediaItem
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import androidx.media3.ui.PlayerView
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors

// Экран больше не создаёт проигрыватель сам, а ПОДКЛЮЧАЕТСЯ к сервису
// PlaybackService (где живут проигрыватель и медиа-сессия) и показывает его.
class MainActivity : Activity() {

    // «Соединение» с сервисом. Готовится асинхронно (не сразу).
    private var controllerFuture: ListenableFuture<MediaController>? = null

    // onStart — экран становится видимым: подключаемся к сервису.
    override fun onStart() {
        super.onStart()

        // Экран проигрывателя.
        val view = PlayerView(this)
        setContentView(view)

        // «Адрес» нашего сервиса с медиа-сессией.
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))

        // Подключаемся к сервису; когда соединение готово — начинаем играть.
        val future = MediaController.Builder(this, token).buildAsync()
        future.addListener({
            val controller = future.get()            // «пульт» к проигрывателю в сервисе
            view.player = controller                  // показываем его на экране
            controller.setMediaItem(MediaItem.fromUri(STREAM_URL))
            controller.prepare()
            controller.play()
        }, MoreExecutors.directExecutor())
        controllerFuture = future
    }

    // onStop — экран скрыт: отключаемся от сервиса (проигрывание в сервисе продолжается).
    override fun onStop() {
        super.onStop()
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
    }

    companion object {
        //   10.0.2.2  — адрес компьютера из эмулятора (где запущен Prism).
        //   после /hls/ — id фильма из http://localhost:8080/api/media
        private const val STREAM_URL =
            "http://10.0.2.2:8080/hls/f8060e10c52e23ad/playlist.m3u8"
    }
}
