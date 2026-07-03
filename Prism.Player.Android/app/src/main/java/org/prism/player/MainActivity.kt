package org.prism.player

import android.app.Activity
import android.os.Bundle
import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView

// Единственный экран: проигрывает одно видео из Prism.
class MainActivity : Activity() {

    // Ссылка на проигрыватель, чтобы освободить его при закрытии экрана.
    private var player: ExoPlayer? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 1. Создаём проигрыватель.
        val exo = ExoPlayer.Builder(this).build()

        // 2. Создаём экран проигрывателя и привязываем к нему проигрыватель.
        val view = PlayerView(this)
        view.player = exo
        setContentView(view)

        // 3. Говорим, что играть (HLS-поток Prism), готовим и запускаем.
        exo.setMediaItem(MediaItem.fromUri(STREAM_URL))
        exo.prepare()
        exo.play()

        player = exo
    }

    // Экран закрывается — освобождаем ресурсы проигрывателя.
    override fun onDestroy() {
        super.onDestroy()
        player?.release()
        player = null
    }

    companion object {
        // ВРЕМЕННО зашитый адрес потока для проверки.
        //   10.0.2.2  — это адрес ХОСТА — компьютера из эмулятора (где запущен Prism).
        //   <ID>      — заменить на id фильма из http://localhost:8080/api/media
        private const val STREAM_URL =
            "http://10.0.2.2:8080/hls/f8060e10c52e23ad/playlist.m3u8"
    }
}
