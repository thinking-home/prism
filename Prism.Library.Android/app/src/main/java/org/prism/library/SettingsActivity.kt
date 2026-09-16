package org.prism.library

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.text.InputType
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

// Экран единственной настройки приложения: подпись, поле ввода адреса
// библиотеки и кнопка «Сохранить». Открывается автоматически, пока адрес не
// задан (см. MainActivity.onStart), а также вручную — кнопкой-шестерёнкой с
// главного экрана, когда адрес уже есть и его нужно поменять.
class SettingsActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Отступы вокруг содержимого экрана, в независимых от плотности экрана пикселях (dp).
        val pad = (16 * resources.displayMetrics.density).toInt()
        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }

        // Подпись поля.
        container.addView(TextView(this).apply { text = getString(R.string.settings_library_url) })

        // Поле ввода адреса — предзаполнено уже сохранённым значением (если есть).
        // TYPE_TEXT_VARIATION_URI просит у системной клавиатуры удобную для URL раскладку.
        val urlField = EditText(this).apply {
            setText(Settings.libraryUrl(this@SettingsActivity))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
        }
        container.addView(urlField)

        val save = Button(this).apply { text = getString(R.string.settings_save) }
        container.addView(save)

        save.setOnClickListener {
            val url = urlField.text.toString().trim()
            if (url.isEmpty()) {
                // Пустой адрес сохранять нельзя: иначе главный экран (см.
                // MainActivity.onStart) снова сочтёт настройку незаданной и
                // откроет этот же экран заново — пользователь застрянет здесь
                // без объяснения причины. Вместо этого — не сохраняем и
                // явно говорим, что нужно ввести адрес.
                Toast.makeText(this, getString(R.string.settings_url_required), Toast.LENGTH_SHORT).show()
            } else {
                Settings.saveLibraryUrl(this, url)
                // Явно открываем главный экран — просто finish() здесь недостаточно:
                // при автоматическом открытии этого экрана (пустой адрес) MainActivity
                // уже закрыл сам себя (см. MainActivity.onStart), и в стеке никого не
                // осталось бы. CLEAR_TOP+SINGLE_TOP работает одинаково для обоих входов:
                // если MainActivity ещё жив в стеке (открывали шестерёнкой) — вернёт
                // существующий экран поверх и уберёт этот; если его уже нет (первый
                // запуск) — создаст новый.
                startActivity(
                    Intent(this, MainActivity::class.java).apply {
                        flags = Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
                    },
                )
                finish()
            }
        }

        // ScrollView — на случай маленького экрана или выехавшей клавиатуры,
        // чтобы поле и кнопка не выезжали за пределы видимой области.
        setContentView(ScrollView(this).apply { addView(container) })
    }
}
