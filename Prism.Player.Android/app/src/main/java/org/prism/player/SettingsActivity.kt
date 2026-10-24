package org.prism.player

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.os.Build
import android.Manifest
import android.content.pm.PackageManager
import android.text.InputType
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Switch
import android.widget.Toast
import androidx.core.content.ContextCompat

// Экран редактирования настроек. Поля с подписями + кнопка «Сохранить».
// Собран из обычных View (как остальной UI), чтобы работал пультом/D-pad на ТВ.
class SettingsActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val pad = (16 * resources.displayMetrics.density).toInt()
        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }

        // Вспомогательный метод: добавить подпись + поле ввода и вернуть само поле.
        fun field(label: String, value: String, type: Int): EditText {
            container.addView(TextView(this).apply { text = label })
            val edit = EditText(this).apply {
                setText(value)
                inputType = type
            }
            container.addView(edit)
            return edit
        }

        val uriType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
        val brokerField = field(getString(R.string.settings_broker), Settings.brokerUrl(this), uriType)
        val userField = field(
            getString(R.string.settings_user), Settings.mqttUser(this), InputType.TYPE_CLASS_TEXT,
        )
        val passField = field(
            getString(R.string.settings_password), Settings.mqttPassword(this),
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD,
        )
        val nameField = field(
            getString(R.string.settings_name), Settings.playerName(this), InputType.TYPE_CLASS_TEXT,
        )

        // Идентификатор плеера — только для чтения (генерируется автоматически).
        container.addView(TextView(this).apply { text = getString(R.string.settings_player_id) })
        container.addView(TextView(this).apply { text = Settings.playerId(this@SettingsActivity) })

        val save = Button(this).apply { text = getString(R.string.settings_save) }
        val remote = Switch(this).apply {
            text = getString(R.string.remote_enabled)
            isChecked = Settings.remoteEnabled(this@SettingsActivity)
        }
        container.addView(remote)
        container.addView(TextView(this).apply { text = getString(R.string.remote_explanation) })
        container.addView(save)

        save.setOnClickListener {
            val broker = brokerField.text.toString().trim()
            if (remote.isChecked && !MqttEndpoint.isValid(broker)) {
                brokerField.error = getString(R.string.invalid_broker)
                brokerField.requestFocus()
                return@setOnClickListener
            }
            Settings.save(
                this,
                broker,
                userField.text.toString().trim(),
                passField.text.toString(),
                nameField.text.toString().trim(),
            )
            Settings.setRemoteEnabled(this, remote.isChecked)
            if (remote.isChecked) {
                if (!MqttService.startIfEnabled(this, MqttService.ACTION_RELOAD)) {
                    Toast.makeText(this, R.string.remote_start_failed, Toast.LENGTH_LONG).show()
                }
                if (Build.VERSION.SDK_INT >= 33 &&
                    ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                    requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
                } else finish()
            } else {
                stopService(Intent(this, MqttService::class.java))
                finish()
            }
        }

        container.addView(Button(this).apply {
            text = getString(R.string.privacy_policy)
            setOnClickListener {
                startActivity(Intent(this@SettingsActivity, PrivacyActivity::class.java))
            }
        })

        setContentView(ScrollView(this).apply { addView(container) })
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 1) {
            if (grantResults.firstOrNull() != PackageManager.PERMISSION_GRANTED) {
                Toast.makeText(this, R.string.notifications_denied, Toast.LENGTH_LONG).show()
            }
            finish()
        }
    }
}
