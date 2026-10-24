package org.prism.player

import android.app.Activity
import android.os.Bundle
import android.graphics.Typeface
import android.text.InputType
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.Toast

class SettingsActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_HIDDEN or
            android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        val content = PlayerUi.page(this, getString(R.string.settings_title))
        fun dp(value: Int) = PlayerUi.dp(this, value)
        fun field(id: Int, label: Int, value: String, type: Int): EditText {
            content.addView(PlayerUi.text(this, getString(label), 13f, muted = true),
                LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(8) })
            val edit = EditText(this).apply {
                this.id = id
                inputType = type
                setSingleLine(true)
                setText(value)
                textSize = 16f
                typeface = Typeface.create("sans-serif", Typeface.NORMAL)
                setTextColor(PlayerUi.TEXT)
                setHintTextColor(PlayerUi.MUTED)
                backgroundTintList = null
                background = PlayerUi.interactive(this@SettingsActivity)
                setPadding(dp(16), dp(10), dp(16), dp(10))
                imeOptions = EditorInfo.IME_ACTION_NEXT
                setSelectAllOnFocus(true)
            }
            content.addView(edit, LinearLayout.LayoutParams(-1, dp(48)).apply { bottomMargin = dp(16) })
            return edit
        }
        val name = field(R.id.player_name, R.string.settings_name, Settings.playerName(this),
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES)
        val broker = field(R.id.broker_url, R.string.settings_broker, Settings.brokerUrl(this),
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI)
        broker.hint = "tcp://192.168.1.10:1883"
        val user = field(R.id.mqtt_user, R.string.settings_user, Settings.mqttUser(this), InputType.TYPE_CLASS_TEXT)
        val password = field(R.id.mqtt_password, R.string.settings_password, Settings.mqttPassword(this),
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        password.transformationMethod = android.text.method.PasswordTransformationMethod.getInstance()
        password.imeOptions = EditorInfo.IME_ACTION_DONE
        val save = PlayerUi.button(this, getString(R.string.settings_save)) {
            val address = broker.text.toString().trim()
            if (!MqttEndpoint.isValid(address)) {
                broker.error = getString(R.string.invalid_broker)
                broker.requestFocus()
            } else {
                Settings.save(this, address, user.text.toString().trim(), password.text.toString(), name.text.toString().trim())
                if (Settings.canRunRemote(this) && !MqttService.startIfEnabled(this, MqttService.ACTION_RELOAD)) {
                    Toast.makeText(this, R.string.remote_start_failed, Toast.LENGTH_LONG).show()
                }
                if (Settings.canRunRemote(this) && android.os.Build.VERSION.SDK_INT >= 33 &&
                    checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
                    requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)
                } else finish()
            }
        }.apply { id = R.id.save_settings }
        content.addView(save, LinearLayout.LayoutParams(-2, dp(48)).apply { topMargin = dp(4) })
        password.setOnEditorActionListener { _, action, _ ->
            if (action == EditorInfo.IME_ACTION_DONE) {
                (getSystemService(INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager)
                    .hideSoftInputFromWindow(password.windowToken, 0)
                save.requestFocus()
                true
            } else false
        }
        name.requestFocus()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 1) finish()
    }
}
