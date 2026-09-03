package org.prism.library

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.util.TypedValue
import android.view.Gravity
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.TextView

// Главный экран приложения. Пока (шаг 2) показывает тот же текст «Hello
// World», что и раньше, плюс кнопку-шестерёнку для перехода к единственной
// настройке — адресу библиотеки. Списки папок появятся на следующих шагах.
class MainActivity : Activity() {

    // onCreate вызывается один раз при создании экрана — здесь мы его собираем.
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // TextView — стандартный элемент Android для показа текста.
        val text = TextView(this).apply {
            text = getString(R.string.hello_world)
            textSize = 24f
        }

        // Кнопка-шестерёнка в правом верхнем углу — открывает экран настроек.
        // Плоский безрамочный фон (штатный selectableItemBackgroundBorderless):
        // прозрачный, с круглой подсветкой при фокусе/нажатии — удобно и для
        // касания, и для пульта.
        val settingsButton = ImageButton(this).apply {
            setImageResource(R.drawable.ic_settings)
            val bg = TypedValue()
            context.theme.resolveAttribute(
                android.R.attr.selectableItemBackgroundBorderless, bg, true,
            )
            setBackgroundResource(bg.resourceId)
            val innerPad = (12 * resources.displayMetrics.density).toInt()
            setPadding(innerPad, innerPad, innerPad, innerPad)
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP or Gravity.END,
            )
            setOnClickListener {
                startActivity(Intent(this@MainActivity, SettingsActivity::class.java))
            }
        }

        // FrameLayout — простой контейнер, умеющий располагать содержимое по
        // краям/центру через Gravity: текст — по центру, кнопка — в углу.
        val root = FrameLayout(this).apply {
            addView(
                text,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    Gravity.CENTER,
                ),
            )
            addView(settingsButton)
        }

        setContentView(root)
    }

    // onStart вызывается каждый раз, когда экран становится видимым — в том
    // числе при возврате с экрана настроек. Проверяем актуальный адрес заново,
    // а не только один раз при создании экрана.
    override fun onStart() {
        super.onStart()

        // Адрес библиотеки ещё не задан — открываем экран настроек вместо
        // главного экрана. Пользователь не сможет пользоваться приложением,
        // пока не введёт адрес (см. settings/spec.md, сценарий «Первый запуск»).
        //
        // finish() здесь обязателен: без него этот экран остаётся под настройками
        // в стеке, и системная кнопка «назад» из настроек просто вернёт сюда же —
        // onStart снова откроет настройки, и выйти из приложения будет нельзя
        // (особенно важно на ТВ, где «назад» — единственный способ уйти пультом).
        // Убрав себя из стека, мы делаем «назад» из настроек обычным выходом.
        if (Settings.libraryUrl(this).isEmpty()) {
            startActivity(Intent(this, SettingsActivity::class.java))
            finish()
        }
    }
}
