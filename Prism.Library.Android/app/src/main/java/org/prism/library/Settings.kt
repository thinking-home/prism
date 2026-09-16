package org.prism.library

import android.content.Context

// Хранилище единственной настройки приложения — базового адреса библиотеки
// Prism. SharedPreferences — простой файл «ключ-значение» внутри приложения;
// значение сохраняется на устройстве и переживает перезапуск приложения.
object Settings {

    private const val PREFS = "prism_library_settings"
    private const val KEY_LIBRARY_URL = "library_url"

    private fun prefs(ctx: Context) =
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    // Сохранённый адрес библиотеки. Пустая строка означает, что настройка ещё
    // не задана — по этому признаку главный экран решает, показывать себя
    // или сразу открыть экран настроек.
    fun libraryUrl(ctx: Context): String =
        prefs(ctx).getString(KEY_LIBRARY_URL, null) ?: ""

    fun saveLibraryUrl(ctx: Context, url: String) {
        prefs(ctx).edit().putString(KEY_LIBRARY_URL, url).apply()
    }
}
