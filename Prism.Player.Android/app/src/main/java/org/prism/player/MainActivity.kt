package org.prism.player

import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.os.SystemClock
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import androidx.media3.ui.PlayerView
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors

// Экран показывает либо видео (когда файл открыт), либо главный экран: логотип по
// центру + кнопка-шестерёнка «Настройки» в правом верхнем углу. Внизу — статус
// брокера; при ошибке — надпись поверх видео.
class MainActivity : Activity() {

    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var playerView: PlayerView? = null
    private var menuView: View? = null
    private var settingsButton: View? = null
    private var statusText: TextView? = null
    private var errorText: TextView? = null
    private var controller: MediaController? = null
    private var lastBackMs = 0L // для «двойного Back» при закрытии фильма

    // onStart — экран становится видимым.
    override fun onStart() {
        super.onStart()

        // Гарантируем, что служба MQTT запущена как «переднего плана».
        startForegroundService(Intent(this, MqttService::class.java))

        val root = FrameLayout(this)

        // Центр: логотип с названием.
        val logo = ImageView(this).apply {
            setImageResource(R.drawable.main_screen)
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER,
            )
        }

        // Настройки — кнопка-шестерёнка в правом верхнем углу. Плоский безрамочный фон
        // (штатный selectableItemBackgroundBorderless): прозрачный, с круглой подсветкой
        // фокуса/нажатия. ImageButton остаётся обычной кнопкой (фокус, клик).
        val settings = ImageButton(this).apply {
            setImageResource(R.drawable.ic_settings)
            val bg = TypedValue()
            context.theme.resolveAttribute(
                android.R.attr.selectableItemBackgroundBorderless, bg, true,
            )
            setBackgroundResource(bg.resourceId)
            val pad = (12 * resources.displayMetrics.density).toInt()
            setPadding(pad, pad, pad, pad)
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP or Gravity.END,
            )
            setOnClickListener {
                startActivity(Intent(this@MainActivity, SettingsActivity::class.java))
            }
        }

        val view = PlayerView(this)
        // Кнопка субтитров (CC) на панели управления: по умолчанию Media3 её
        // прячет, без неё субтитры с пульта не включить.
        view.setShowSubtitleButton(true)

        // Надпись об ошибке — по центру, поверх видео, скрыта до ошибки.
        val error = TextView(this).apply {
            text = getString(R.string.error_playback)
            textSize = 20f
            visibility = View.GONE
            setTextColor(0xFFFFFFFF.toInt())
            setBackgroundColor(0xCC000000.toInt())
            val p = (12 * resources.displayMetrics.density).toInt()
            setPadding(p, p, p, p)
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER,
            )
        }
        // Мелкий статус связи с брокером — у нижнего края.
        val status = TextView(this).apply {
            textSize = 12f
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL,
            )
        }

        root.addView(logo)
        root.addView(view, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT,
        )) // видео на весь экран
        root.addView(error)
        root.addView(status)
        root.addView(settings) // поверх, в правом верхнем углу
        setContentView(root)
        playerView = view
        menuView = logo
        settingsButton = settings
        errorText = error
        statusText = status
        settings.requestFocus() // фокус на шестерёнку — для пульта/D-pad

        // Показываем текущий статус брокера и слушаем изменения, пока экран открыт.
        updateStatus(MqttStatus.connected)
        MqttStatus.listener = { connected -> runOnUiThread { updateStatus(connected) } }

        // Подключаемся к сервису с медиа-сессией.
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        val future = MediaController.Builder(this, token).buildAsync()
        future.addListener({
            val c = future.get()
            controller = c
            view.player = c
            // Реагируем на открытие/закрытие файла, чтобы переключать меню/видео.
            c.addListener(object : Player.Listener {
                override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                    errorText?.visibility = View.GONE // новый файл — прячем прошлую ошибку
                    updateUi(c)
                }
                override fun onPlaybackStateChanged(playbackState: Int) {
                    // Фильм доиграл до конца → закрываем его сами (возврат в меню).
                    if (playbackState == Player.STATE_ENDED) c.clearMediaItems()
                    updateUi(c)
                }
                override fun onPlayerError(error: PlaybackException) {
                    errorText?.visibility = View.VISIBLE
                }
            })
            updateUi(c) // начальное состояние
        }, MoreExecutors.directExecutor())
        controllerFuture = future
    }

    // Файл открыт → показываем видео; иначе — главный экран (логотип + шестерёнка + статус).
    private fun updateUi(controller: MediaController) {
        val fileOpen = controller.currentMediaItem != null
        playerView?.visibility = if (fileOpen) View.VISIBLE else View.GONE
        menuView?.visibility = if (fileOpen) View.GONE else View.VISIBLE
        settingsButton?.visibility = if (fileOpen) View.GONE else View.VISIBLE
        statusText?.visibility = if (fileOpen) View.GONE else View.VISIBLE
        if (!fileOpen) settingsButton?.requestFocus()
        // Если ошибка случилась до подключения к сессии — показать надпись сейчас.
        if (controller.playerError != null) errorText?.visibility = View.VISIBLE
    }

    private fun updateStatus(connected: Boolean) {
        statusText?.text = getString(if (connected) R.string.mqtt_connected else R.string.mqtt_disconnected)
    }

    // Back при открытом фильме — закрыть по ДВОЙНОМУ нажатию (чтобы случайно не сбросить
    // прогресс): первый Back — подсказка, второй в течение 3 с — закрыть и вернуться в меню.
    // Из меню Back — обычный выход из приложения.
    override fun onBackPressed() {
        val c = controller
        if (c == null || c.currentMediaItem == null) {
            super.onBackPressed()
            return
        }
        val now = SystemClock.elapsedRealtime()
        if (now - lastBackMs < 3000) {
            c.clearMediaItems() // второй Back — закрыть фильм
        } else {
            lastBackMs = now
            Toast.makeText(this, getString(R.string.back_to_close), Toast.LENGTH_SHORT).show()
        }
    }

    // onStop — экран скрыт: пауза и отключение от сервиса (сервис/служба живут дальше).
    override fun onStop() {
        super.onStop()
        controller?.pause() // пауза при сворачивании плеера в фон
        MqttStatus.listener = null
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
        controller = null
    }
}
