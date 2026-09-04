package org.prism.library

import android.content.Context
import android.util.TypedValue
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView

// Одна строка списка папки — либо группа (подпапка), либо файл. sealed class
// вместо двух разных списков: элементы рисуются одной RecyclerView одним
// адаптером, порядок задаём при сборке списка (см. MainActivity).
sealed class LibraryListItem {
    data class Group(val node: LibraryNode) : LibraryListItem()
    data class File(val media: MediaCard) : LibraryListItem()
}

// Адаптер RecyclerView: превращает список LibraryListItem в строки экрана.
// Строка — обычный TextView, созданный кодом (без XML-разметки — так собран
// весь остальной интерфейс приложения).
class LibraryListAdapter(private val items: List<LibraryListItem>) :
    RecyclerView.Adapter<LibraryListAdapter.ViewHolder>() {

    class ViewHolder(val text: TextView) : RecyclerView.ViewHolder(text)

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
        val context: Context = parent.context
        val pad = (16 * context.resources.displayMetrics.density).toInt()
        val text = TextView(context).apply {
            setPadding(pad, pad, pad, pad)
            textSize = 18f
            // focusable — обязательно для пульта: без этого D-pad не сможет
            // перевести выделение на строку списка (см. design.md).
            isFocusable = true
            isFocusableInTouchMode = false
            val bg = TypedValue()
            context.theme.resolveAttribute(android.R.attr.selectableItemBackground, bg, true)
            setBackgroundResource(bg.resourceId)
        }
        return ViewHolder(text)
    }

    override fun onBindViewHolder(holder: ViewHolder, position: Int) {
        when (val item = items[position]) {
            // 📁 — группа (подпапка); переход внутрь появится на следующем шаге.
            is LibraryListItem.Group -> holder.text.text = "📁 ${item.node.name}"
            // 🎬 — файл; недоступные (present=false) файлы появятся на шаге 5.
            is LibraryListItem.File -> holder.text.text = "🎬 ${item.media.title}"
        }
    }

    override fun getItemCount(): Int = items.size
}
