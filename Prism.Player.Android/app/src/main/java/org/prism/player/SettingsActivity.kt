package org.prism.player

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.text.InputType
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

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
        val brokerField = field("Адрес MQTT-брокера", Settings.brokerUrl(this), uriType)
        val userField = field(
            "Логин MQTT (необязательно)", Settings.mqttUser(this), InputType.TYPE_CLASS_TEXT,
        )
        val passField = field(
            "Пароль MQTT (необязательно)", Settings.mqttPassword(this),
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD,
        )
        val nameField = field(
            "Отображаемое имя", Settings.playerName(this), InputType.TYPE_CLASS_TEXT,
        )

        // Идентификатор плеера — только для чтения (генерируется автоматически).
        container.addView(TextView(this).apply { text = "ID плеера (не редактируется):" })
        container.addView(TextView(this).apply { text = Settings.playerId(this@SettingsActivity) })

        val save = Button(this).apply { text = "Сохранить" }
        container.addView(save)

        save.setOnClickListener {
            Settings.save(
                this,
                brokerField.text.toString().trim(),
                userField.text.toString().trim(),
                passField.text.toString(),
                nameField.text.toString().trim(),
            )
            // Перезапускаем службу MQTT — она перечитает брокер/id и переподключится.
            stopService(Intent(this, MqttService::class.java))
            startForegroundService(Intent(this, MqttService::class.java))
            finish()
        }

        setContentView(ScrollView(this).apply { addView(container) })
    }
}
