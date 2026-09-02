package org.prism.library

import android.app.Activity
import android.os.Bundle
import android.view.Gravity
import android.widget.FrameLayout
import android.widget.TextView

// Первый, самый простой экран приложения: один текст по центру.
// Дальше сюда добавятся настройка адреса библиотеки и список папок —
// но на этом шаге ничего, кроме надписи, быть не должно.
class MainActivity : Activity() {

    // onCreate вызывается один раз при создании экрана — здесь мы его собираем.
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // TextView — стандартный элемент Android для показа текста.
        val text = TextView(this).apply {
            text = getString(R.string.hello_world)
            textSize = 24f
        }

        // FrameLayout — простой контейнер, умеющий располагать содержимое по
        // краям/центру через Gravity. Здесь в нём один-единственный TextView,
        // выровненный по центру экрана.
        val root = FrameLayout(this).apply {
            addView(
                text,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    Gravity.CENTER,
                ),
            )
        }

        setContentView(root)
    }
}
