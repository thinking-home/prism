package org.prism.player

import android.os.Bundle
import android.content.ComponentName
import android.content.Intent
import android.os.SystemClock
import android.os.Build
import android.Manifest
import android.content.pm.PackageManager
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.view.Gravity
import android.view.View
import android.view.KeyEvent
import android.widget.FrameLayout
import android.widget.ArrayAdapter
import androidx.appcompat.widget.ListPopupWindow
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
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
class MainActivity : AppCompatActivity() {

    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var playerView: PlayerView? = null
    private var menuView: View? = null
    private var settingsButton: View? = null
    private var showSettingsFocus = false
    private val focusHandler = android.os.Handler(android.os.Looper.getMainLooper())
    private val hideHomeFocus = Runnable {
        if (isVisible && controller?.currentMediaItem == null && navigationMenu?.isShowing != true) {
            setHomeFocusVisible(false)
        }
    }
    private var navigationMenu: ListPopupWindow? = null
    private var statusText: TextView? = null
    private var statusPanel: View? = null
    private var recoveryButton: Button? = null
    private var errorText: TextView? = null
    private var controller: MediaController? = null
    private var lastBackMs = 0L // для «двойного Back» при закрытии фильма
    private val statusListener: () -> Unit = { updateStatus() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (controller?.currentMediaItem == null) {
                    isEnabled = false
                    onBackPressedDispatcher.onBackPressed()
                    isEnabled = true
                } else {
                    closePlaybackOnBack()
                }
            }
        })
    }

    // onStart — экран становится видимым.
    @androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
    override fun onStart() {
        super.onStart()
        focusHandler.removeCallbacks(hideHomeFocus)
        showSettingsFocus = false

        // Гарантируем, что служба MQTT запущена как «переднего плана».
        isVisible = true
        MqttService.startIfEnabled(this, MqttService.ACTION_VISIBLE)

        val root = FrameLayout(this)

        // Центр: логотип без текста.
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
            id = View.generateViewId()
            contentDescription = getString(R.string.settings_title)
            setImageResource(R.drawable.ic_settings)
            setColorFilter(PlayerUi.TEXT)
            // Keep real focus for immediate OK activation, but leave the home
            // screen quiet until the user starts navigating with the D-pad.
            background = PlayerUi.surface(this@MainActivity, android.graphics.Color.TRANSPARENT)
            val pad = PlayerUi.dp(this@MainActivity, 12)
            setPadding(pad, pad, pad, pad)
            layoutParams = FrameLayout.LayoutParams(
                PlayerUi.dp(this@MainActivity, 48), PlayerUi.dp(this@MainActivity, 48),
                Gravity.TOP or Gravity.END,
            ).apply { topMargin = PlayerUi.dp(this@MainActivity, 24); marginEnd = PlayerUi.dp(this@MainActivity, 32) }
            setOnClickListener {
                showNavigationMenu(this)
            }
        }

        val view = PlayerView(this)
        // Кнопка субтитров (CC) на панели управления: по умолчанию Media3 её
        // прячет, без неё субтитры с пульта не включить.
        view.setShowSubtitleButton(true)
        PlaybackUi.apply(view)
        // Чёрный фон под видео: иначе в полях по краям кадра (когда пропорции
        // фильма не совпадают с экраном) просвечивает серый фон приложения.
        view.setBackgroundColor(0xFF000000.toInt())

        // Надпись об ошибке — по центру, поверх видео, скрыта до ошибки.
        val error = TextView(this).apply {
            text = getString(R.string.error_playback)
            textSize = 18f
            visibility = View.GONE
            setTextColor(PlayerUi.TEXT)
            background = PlayerUi.surface(this@MainActivity, 0xE6323232.toInt())
            val p = (12 * resources.displayMetrics.density).toInt()
            setPadding(p, p, p, p)
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER,
            )
        }
        val status = PlayerUi.text(this, "", 13f, muted = true).apply { gravity = Gravity.CENTER }
        val recovery = PlayerUi.button(this, getString(R.string.remote_enable_action)) {
            if (!MqttEndpoint.isValid(Settings.brokerUrl(this))) {
                startActivity(Intent(this, SettingsActivity::class.java))
            } else {
                Settings.setRemoteEnabled(this, true)
                if (!MqttService.startIfEnabled(this, MqttService.ACTION_RELOAD)) {
                    Settings.setRemoteEnabled(this, false)
                    Toast.makeText(this, R.string.remote_start_failed, Toast.LENGTH_LONG).show()
                } else if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                    requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
                }
                updateStatus()
            }
        }.apply {
            id = View.generateViewId()
            nextFocusUpId = settings.id
        }
        settings.nextFocusDownId = recovery.id
        val footer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            addView(status)
            addView(recovery, LinearLayout.LayoutParams(-2, -2).apply { topMargin = PlayerUi.dp(this@MainActivity, 12) })
            layoutParams = FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM).apply {
                bottomMargin = PlayerUi.dp(this@MainActivity, 32)
                leftMargin = PlayerUi.dp(this@MainActivity, 32)
                rightMargin = PlayerUi.dp(this@MainActivity, 32)
            }
        }
        root.addView(logo)
        root.addView(view, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT,
        )) // видео на весь экран
        root.addView(error)
        root.addView(footer)
        root.addView(settings) // поверх, в правом верхнем углу
        setContentView(root)
        playerView = view
        menuView = logo
        settingsButton = settings
        errorText = error
        statusText = status
        statusPanel = footer
        recoveryButton = recovery
        settings.requestFocus() // фокус на шестерёнку — для пульта/D-pad

        // Показываем текущий статус брокера и слушаем изменения, пока экран открыт.
        updateStatus()
        MqttStatus.addListener(statusListener)

        // Подключаемся к сервису с медиа-сессией.
        val token = SessionToken(this, ComponentName(this, PlaybackService::class.java))
        val future = MediaController.Builder(this, token).buildAsync()
        controllerFuture = future
        future.addListener({
            if (controllerFuture !== future) return@addListener
            val c = try { future.get() } catch (_: Exception) {
                errorText?.visibility = View.VISIBLE
                return@addListener
            }
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
    }

    // Файл открыт → показываем видео; иначе — главный экран (логотип + шестерёнка + статус).
    private fun updateUi(controller: MediaController) {
        val fileOpen = controller.currentMediaItem != null
        playerView?.visibility = if (fileOpen) View.VISIBLE else View.GONE
        menuView?.visibility = if (fileOpen) View.GONE else View.VISIBLE
        settingsButton?.visibility = if (fileOpen) View.GONE else View.VISIBLE
        if (fileOpen) navigationMenu?.dismiss()
        statusPanel?.visibility = if (fileOpen) View.GONE else View.VISIBLE
        if (!fileOpen && navigationMenu?.isShowing != true && recoveryButton?.hasFocus() != true) settingsButton?.requestFocus()
        // Если ошибка случилась до подключения к сессии — показать надпись сейчас.
        if (controller.playerError != null) errorText?.visibility = View.VISIBLE
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        // Hidden focus must not activate an invisible selection, including after
        // the idle timeout. Consume both key events so View cannot click on UP.
        if (!showSettingsFocus && controller?.currentMediaItem == null &&
            navigationMenu?.isShowing != true && event.keyCode in intArrayOf(
                KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER)) {
            return true
        }
        if (event.action == KeyEvent.ACTION_DOWN &&
            controller?.currentMediaItem == null && navigationMenu?.isShowing != true) {
            if (event.keyCode in intArrayOf(KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN,
                    KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT)) {
                setHomeFocusVisible(true)
            }
            restartFocusTimeout()
        }
        return super.dispatchKeyEvent(event)
    }

    override fun onUserInteraction() {
        super.onUserInteraction()
        restartFocusTimeout()
    }

    private fun setHomeFocusVisible(visible: Boolean) {
        showSettingsFocus = visible
        settingsButton?.background = if (visible) PlayerUi.interactive(this, android.graphics.Color.TRANSPARENT)
            else PlayerUi.surface(this, android.graphics.Color.TRANSPARENT)
        recoveryButton?.background = if (visible) PlayerUi.interactive(this)
            else PlayerUi.surface(this, PlayerUi.SURFACE)
    }

    private fun restartFocusTimeout() {
        focusHandler.removeCallbacks(hideHomeFocus)
        if (isVisible && showSettingsFocus && controller?.currentMediaItem == null && navigationMenu?.isShowing != true) {
            focusHandler.postDelayed(hideHomeFocus, 8_000L)
        }
    }

    private fun showNavigationMenu(anchor: View) {
        navigationMenu?.dismiss()
        val popup = ListPopupWindow(this).apply {
            anchorView = anchor
            verticalOffset = -anchor.height
            setDropDownGravity(Gravity.END)
            width = minOf((240 * resources.displayMetrics.density).toInt(), resources.displayMetrics.widthPixels)
            isModal = true
            setBackgroundDrawable(PlayerUi.surface(this@MainActivity, PlayerUi.SURFACE))
            setListSelector(PlayerUi.interactive(this@MainActivity, android.graphics.Color.TRANSPARENT))
            setAdapter(object : ArrayAdapter<String>(this@MainActivity, android.R.layout.simple_list_item_1,
                listOf(getString(R.string.settings_title), getString(R.string.about_title))) {
                override fun getView(position: Int, convertView: View?, parent: ViewGroup): View =
                    PlayerUi.text(this@MainActivity, getItem(position).orEmpty()).apply {
                        gravity = Gravity.CENTER_VERTICAL
                        setPadding(PlayerUi.dp(context, 20), PlayerUi.dp(context, 12), PlayerUi.dp(context, 20), PlayerUi.dp(context, 12))
                        minHeight = PlayerUi.dp(context, 52)
                    }
            })
            setOnItemClickListener { _, _, position, _ ->
                dismiss()
                val target = if (position == 0) SettingsActivity::class.java else AboutActivity::class.java
                startActivity(Intent(this@MainActivity, target))
            }
            setOnDismissListener {
                anchor.alpha = 1f
                anchor.requestFocus()
                restartFocusTimeout()
            }
        }
        navigationMenu = popup
        popup.show()
        focusHandler.removeCallbacks(hideHomeFocus)
        anchor.alpha = 0f
        popup.setSelection(0)
    }

    private fun updateStatus() {
        statusText?.text = getString(MqttStatus.textResource(this))
        val configured = MqttEndpoint.isValid(Settings.brokerUrl(this))
        val needsAction = !configured || Settings.remoteExplicitlyStopped(this)
        recoveryButton?.apply {
            val hadFocus = hasFocus()
            setText(if (configured) R.string.remote_enable_action else R.string.setup_action)
            visibility = if (needsAction) View.VISIBLE else View.GONE
            if (hadFocus && !needsAction) settingsButton?.requestFocus()
        }
    }

    // Back при открытом фильме — закрыть по ДВОЙНОМУ нажатию (чтобы случайно не сбросить
    // прогресс): первый Back — подсказка, второй в течение 3 с — закрыть и вернуться в меню.
    // Из меню Back — обычный выход из приложения.
    private fun closePlaybackOnBack() {
        val c = controller ?: return
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
        navigationMenu?.dismiss()
        navigationMenu = null
        focusHandler.removeCallbacks(hideHomeFocus)
        isVisible = false
        super.onStop()
        controller?.pause() // пауза при сворачивании плеера в фон
        MqttStatus.removeListener(statusListener)
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
        controller = null
    }

    companion object {
        var isVisible = false
            private set
    }
}
