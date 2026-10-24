package org.prism.player

import android.content.res.ColorStateList
import android.graphics.Typeface
import android.view.View
import android.view.ViewGroup
import android.widget.ImageButton
import android.widget.TextView
import androidx.media3.ui.DefaultTimeBar
import androidx.media3.ui.PlayerView

/** Skin the stock Media3 controls without changing geometry, listeners or focus order. */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
object PlaybackUi {
    fun apply(playerView: PlayerView) {
        val controls = playerView.findViewById<View>(androidx.media3.ui.R.id.exo_controller) ?: return
        fun style(view: View) {
            if (view is ImageButton || (view is TextView && view.isClickable)) {
                // Background replacement must preserve the stock icon/text padding.
                val left = view.paddingLeft
                val top = view.paddingTop
                val right = view.paddingRight
                val bottom = view.paddingBottom
                view.backgroundTintList = null
                (view as? androidx.core.view.TintableBackgroundView)?.supportBackgroundTintList = null
                view.setBackgroundResource(R.drawable.player_control_focus)
                view.setPadding(left, top, right, bottom)
                view.stateListAnimator = null
            }
            if (view is ImageButton) view.imageTintList = ColorStateList.valueOf(PlayerUi.TEXT)
            if (view is TextView) {
                view.typeface = Typeface.create("sans-serif", Typeface.NORMAL)
                view.setTextColor(if (view.id == androidx.media3.ui.R.id.exo_duration) PlayerUi.MUTED else PlayerUi.TEXT)
            }
            if (view is DefaultTimeBar) {
                view.setPlayedColor(PlayerUi.TEXT)
                view.setScrubberColor(PlayerUi.TEXT)
                view.setBufferedColor(0xFF888888.toInt())
                view.setUnplayedColor(0xFF505050.toInt())
                view.setAdMarkerColor(PlayerUi.MUTED)
                view.setPlayedAdMarkerColor(0xFF707070.toInt())
            }
            if (view is ViewGroup) for (index in 0 until view.childCount) style(view.getChildAt(index))
        }
        style(controls)
    }
}
