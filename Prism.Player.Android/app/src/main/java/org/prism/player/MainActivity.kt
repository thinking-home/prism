package org.prism.player

import android.app.Activity
import android.os.Bundle
import android.widget.TextView

// Activity — это один экран приложения.
// MainActivity — наш единственный экран.
class MainActivity : Activity() {

    // onCreate вызывается системой, когда экран создаётся.
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Создаём текстовую надпись и показываем её на весь экран.
        val text = TextView(this)
        text.text = "Prism Player"
        text.textSize = 32f
        setContentView(text)
    }
}
