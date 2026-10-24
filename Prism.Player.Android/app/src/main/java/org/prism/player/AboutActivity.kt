package org.prism.player

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.Toast

class AboutActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        fun dp(value: Int) = PlayerUi.dp(this, value)
        val content = PlayerUi.page(this, getString(R.string.about_title))
        val identity = LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(20), 0, dp(32))
        }
        identity.addView(ImageView(this).apply {
            setImageResource(R.drawable.prism_logo)
            scaleType = ImageView.ScaleType.FIT_CENTER
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }, LinearLayout.LayoutParams(dp(48), dp(80)).apply { marginEnd = dp(28) })
        val name = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        name.addView(PlayerUi.text(this, getString(R.string.about_app_name), 26f))
        name.addView(PlayerUi.text(this, getString(R.string.about_version, BuildConfig.VERSION_NAME), 14f, true),
            LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(12) })
        identity.addView(name, LinearLayout.LayoutParams(0, -2, 1f))
        content.addView(identity)
        content.addView(PlayerUi.text(this, getString(R.string.about_player_id), 13f, true))
        content.addView(PlayerUi.text(this, Settings.playerId(this), 16f).apply {
            setPadding(0, dp(8), 0, dp(32))
        })
        val privacy = PlayerUi.button(this, getString(R.string.privacy_policy), quiet = true) {
            val url = "https://prism.thinking-home.ru/player-privacy"
            try {
                startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
            } catch (_: ActivityNotFoundException) {
                Toast.makeText(this, getString(R.string.browser_unavailable_url, url), Toast.LENGTH_LONG).show()
            }
        }.apply {
            gravity = Gravity.CENTER_VERTICAL or Gravity.START
            setCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, R.drawable.ic_external, 0)
            compoundDrawablePadding = dp(24)
        }
        content.addView(privacy, LinearLayout.LayoutParams(-1, -2))
        privacy.requestFocus()
    }
}
