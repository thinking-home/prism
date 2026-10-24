package org.prism.player

import android.app.Activity
import android.os.Bundle
import android.widget.ScrollView
import android.widget.TextView

// Bundled text remains readable on a TV without a browser or internet connection.
class PrivacyActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val language = if (resources.configuration.locales[0].language == "ru") "ru" else "en"
        val policy = assets.open("privacy-$language.txt").bufferedReader().use { it.readText() }
        val padding = (24 * resources.displayMetrics.density).toInt()
        val content = TextView(this).apply {
            text = policy
            textSize = 18f
            setPadding(padding, padding, padding, padding)
        }
        val scroll = ScrollView(this).apply {
            isFocusable = true
            addView(content)
        }
        setContentView(scroll)
        scroll.requestFocus()
    }
}
