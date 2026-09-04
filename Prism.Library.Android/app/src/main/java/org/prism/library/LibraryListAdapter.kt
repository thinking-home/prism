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
// весь остальной интерфейс приложения). onGroupClick вызывается при выборе
// строки-группы — переход внутрь неё делает MainActivity (стек папок).
// У файлов действий пока нет — они появятся на следующих шагах (шаги 6–7).
class LibraryListAdapter(
    private val items: List<LibraryListItem>,
    private val onGroupClick: (LibraryNode) -> Unit,
) : RecyclerView.Adapter<LibraryListAdapter.ViewHolder>() {

    class ViewHolder(val text: TextView) : RecyclerView.ViewHolder(text)

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
        val context: Context = parent.context
        val pad = (16 * context.resources.displayMetrics.density).toInt()
        val text = TextView(context).apply {
            setPadding(pad, pad, pad, pad)
            // Отступ между иконкой строки (папка/файл) и текстом названия.
            compoundDrawablePadding = pad
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
        // Иконка слева от текста строки — компактный способ показать значок
        // без отдельного ImageView в разметке (вся разметка строки — один
        // TextView, см. onCreateViewHolder).
        when (val item = items[position]) {
            // Группа (подпапка): клик/центр пульта заходит внутрь неё.
            is LibraryListItem.Group -> {
                holder.text.text = item.node.name
                holder.text.setCompoundDrawablesWithIntrinsicBounds(R.drawable.ic_folder, 0, 0, 0)
                holder.text.setOnClickListener { onGroupClick(item.node) }
            }
            // Файл; недоступные (present=false) файлы появятся на шаге 5,
            // действия с файлом (информация, запуск на плеере) — шаги 6–7.
            is LibraryListItem.File -> {
                holder.text.text = item.media.title
                holder.text.setCompoundDrawablesWithIntrinsicBounds(R.drawable.ic_file, 0, 0, 0)
                holder.text.setOnClickListener(null)
            }
        }
    }

    override fun getItemCount(): Int = items.size
}
