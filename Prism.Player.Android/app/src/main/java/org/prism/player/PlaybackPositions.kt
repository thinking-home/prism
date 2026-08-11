package org.prism.player

import android.content.Context
import android.content.SharedPreferences

// Локальная память позиций просмотра («продолжить с места»): url → позиция.
// Хранится только на устройстве, в SharedPreferences; значение — «мс|когда».
// История ограничена: старые записи вытесняются.
object PlaybackPositions {

    private const val PREFS = "positions"
    private const val MAX_ENTRIES = 100
    private const val MIN_POSITION_MS = 10_000L // совсем начало не запоминаем
    private const val FINISHED_FRACTION = 0.95  // дальше — считаем досмотренным

    private fun prefs(context: Context): SharedPreferences =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    // Сохранённая позиция для url в миллисекундах (или null — нет записи).
    fun get(context: Context, url: String): Long? {
        val raw = prefs(context).getString(url, null) ?: return null
        return raw.substringBefore('|').toLongOrNull()
    }

    // Запомнить позицию. В самом начале и почти в конце фильма запись, наоборот,
    // удаляется: продолжать нечего, следующий запуск — с начала.
    fun save(context: Context, url: String, positionMs: Long, durationMs: Long) {
        val finished = durationMs > 0 && positionMs >= durationMs * FINISHED_FRACTION
        if (positionMs < MIN_POSITION_MS || finished) {
            clear(context, url)
            return
        }
        val p = prefs(context)
        if (!p.contains(url)) prune(p)
        p.edit().putString(url, "$positionMs|${System.currentTimeMillis()}").apply()
    }

    fun clear(context: Context, url: String) {
        prefs(context).edit().remove(url).apply()
    }

    // Перед добавлением новой записи убираем самые старые сверх лимита.
    private fun prune(p: SharedPreferences) {
        val all = p.all
        if (all.size < MAX_ENTRIES) return
        val oldest = all.entries
            .sortedBy { (it.value as? String)?.substringAfter('|')?.toLongOrNull() ?: 0L }
            .take(all.size - MAX_ENTRIES + 1)
        val e = p.edit()
        for (entry in oldest) e.remove(entry.key)
        e.apply()
    }
}
