package org.prism.player

import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import androidx.media3.ui.PlayerView
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors

// Экран подключается к сервису и показывает либо видео (когда файл открыт),
// либо заглушку с названием приложения (когда файла нет).
class MainActivity : Activity() {

    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var playerView: PlayerView? = null
    private var menuView: View? = null

    // onStart — экран становится видимым.
    override fun onStart() {
        super.onStart()

        // Гарантируем, что служба MQTT запущена как «переднего плана» (чтобы слушала
        // команды даже после закрытия приложения). Если уже запущена — ничего не будет.
        startForegroundService(Intent(this, MqttService::class.java))

        // Контейнер: по центру — «меню» (название + кнопка настроек), поверх — видео.
        val root = FrameLayout(this)
        val menu = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER,
            )
        }
        menu.addView(TextView(this).apply {
            text = "Prism Player"
            textSize = 32f
        })
        val settings = Button(this).apply {
            text = "Настройки"
            setOnClickListener {
                startActivity(Intent(this@MainActivity, SettingsActivity::class.java))
            }
        }
        menu.addView(settings)

        val view = PlayerView(this)
        root.addView(menu)
        root.addView(view)
        setContentView(root)
        playerView = view
        menuView = menu
        settings.requestFocus() // сразу фокус на кнопку — для пульта/D-pad на ТВ

        // Подключаемся к сервису с медиа-сессией.
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        val future = MediaController.Builder(this, token).buildAsync()
        future.addListener({
            val controller = future.get()
            view.player = controller
            // Реагируем на открытие/закрытие файла, чтобы переключать заглушку/видео.
            controller.addListener(object : Player.Listener {
                override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) = updateUi(controller)
                override fun onPlaybackStateChanged(playbackState: Int) = updateUi(controller)
            })
            updateUi(controller) // начальное состояние
        }, MoreExecutors.directExecutor())
        controllerFuture = future
    }

    // Файл открыт, если у плеера есть текущий элемент: показываем видео, иначе — заглушку.
    private fun updateUi(controller: MediaController) {
        val fileOpen = controller.currentMediaItem != null
        playerView?.visibility = if (fileOpen) View.VISIBLE else View.GONE
        menuView?.visibility = if (fileOpen) View.GONE else View.VISIBLE
    }

    // onStop — экран скрыт: отключаемся от сервиса (сервис и проигрывание живут дальше).
    override fun onStop() {
        super.onStop()
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
    }
}
