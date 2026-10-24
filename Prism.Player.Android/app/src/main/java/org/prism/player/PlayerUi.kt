package org.prism.player

import android.app.Activity
import android.content.Context
import android.graphics.Rect
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.StateListDrawable
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/** Shared visual language for the TV screens, including visible D-pad focus. */
object PlayerUi {
    const val TEXT = 0xFFE7E7E7.toInt()
    const val MUTED = 0xFFABABAB.toInt()
    const val SURFACE = 0xFF323232.toInt()
    const val FOCUS = 0xFF414D66.toInt()
    fun dp(ctx: Context, value: Int) = (value * ctx.resources.displayMetrics.density).toInt()

    fun surface(ctx: Context, color: Int) = GradientDrawable().apply {
        setColor(color)
        cornerRadius = dp(ctx, 10).toFloat()
    }

    fun interactive(ctx: Context, normal: Int = SURFACE) = StateListDrawable().apply {
        addState(intArrayOf(android.R.attr.state_focused), surface(ctx, FOCUS))
        addState(intArrayOf(android.R.attr.state_selected), surface(ctx, FOCUS))
        addState(intArrayOf(android.R.attr.state_pressed), surface(ctx, FOCUS))
        addState(intArrayOf(), surface(ctx, normal))
    }

    fun text(ctx: Context, value: String, size: Float = 16f, muted: Boolean = false) = TextView(ctx).apply {
        text = value
        textSize = size
        typeface = Typeface.create("sans-serif", Typeface.NORMAL)
        setTextColor(if (muted) MUTED else TEXT)
        includeFontPadding = false
    }

    fun button(ctx: Context, value: String, quiet: Boolean = false, action: () -> Unit) = Button(ctx).apply {
        text = value
        textSize = 16f
        typeface = Typeface.create("sans-serif", Typeface.NORMAL)
        isAllCaps = false
        letterSpacing = 0f
        includeFontPadding = false
        setTextColor(TEXT)
        minHeight = dp(ctx, 48)
        minimumHeight = dp(ctx, 48)
        setPadding(dp(ctx, 20), dp(ctx, 10), dp(ctx, 20), dp(ctx, 10))
        backgroundTintList = null
        background = interactive(ctx, if (quiet) android.graphics.Color.TRANSPARENT else SURFACE)
        stateListAnimator = null
        setOnClickListener { action() }
    }

    fun page(activity: Activity, title: String, width: Int = 560): LinearLayout {
        val root = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundResource(R.color.bg_dark)
            setPadding(dp(activity, 32), dp(activity, 20), dp(activity, 32), 0)
        }
        val header = LinearLayout(activity).apply { gravity = Gravity.CENTER_VERTICAL }
        header.addView(ImageButton(activity).apply {
            contentDescription = activity.getString(R.string.navigate_back)
            setImageResource(R.drawable.ic_back)
            setPadding(dp(activity, 12), dp(activity, 12), dp(activity, 12), dp(activity, 12))
            background = interactive(activity, android.graphics.Color.TRANSPARENT)
            setOnClickListener { activity.finish() }
        }, LinearLayout.LayoutParams(dp(activity, 48), dp(activity, 48)))
        header.addView(text(activity, title, 24f), LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT,
        ).apply { marginStart = dp(activity, 16) })
        root.addView(header)
        val content = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(activity, 4), dp(activity, 20), dp(activity, 4), dp(activity, 28))
        }
        val centered = LinearLayout(activity).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            addView(content, LinearLayout.LayoutParams(
                minOf(dp(activity, width), activity.resources.displayMetrics.widthPixels - dp(activity, 64)),
                LinearLayout.LayoutParams.WRAP_CONTENT,
            ))
        }
        root.addView(ScrollView(activity).apply {
            isFillViewport = true
            clipToPadding = false
            addView(centered)
        }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f))
        activity.setContentView(root)
        // Fullscreen windows do not always resize for a TV keyboard. Keep the
        // scrolling content inside the visible area without changing stored data.
        root.viewTreeObserver.addOnGlobalLayoutListener {
            val visible = Rect()
            root.getWindowVisibleDisplayFrame(visible)
            val location = IntArray(2)
            root.getLocationOnScreen(location)
            val obscured = (location[1] + root.height - visible.bottom).coerceAtLeast(0)
            val bottom = if (obscured > dp(activity, 100)) obscured else 0
            if (root.paddingBottom != bottom) root.setPadding(root.paddingLeft, root.paddingTop, root.paddingRight, bottom)
        }
        return content
    }
}
