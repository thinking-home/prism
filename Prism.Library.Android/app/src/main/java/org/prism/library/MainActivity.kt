package org.prism.library

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView

// Главный экран приложения: список содержимого текущей папки библиотеки (на
// этом шаге — только корень, без перехода внутрь групп) плюс кнопка-шестерёнка
// для перехода к настройке адреса.
class MainActivity : Activity() {

    private lateinit var statusText: TextView
    private lateinit var list: RecyclerView

    // onCreate вызывается один раз при создании экрана — здесь мы его собираем.
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Список содержимого папки — заполняется после загрузки с сервера
        // (см. loadRoot). LinearLayoutManager — элементы друг под другом.
        list = RecyclerView(this).apply {
            layoutManager = LinearLayoutManager(this@MainActivity)
        }

        // Текст состояния — «загрузка…» или сообщение об ошибке. Показывается
        // поверх списка по центру, пока список пуст/не загружен.
        statusText = TextView(this).apply {
            textSize = 18f
        }

        // Кнопка-шестерёнка в правом верхнем углу — открывает экран настроек.
        // Плоский безрамочный фон (штатный selectableItemBackgroundBorderless):
        // прозрачный, с круглой подсветкой при фокусе/нажатии — удобно и для
        // касания, и для пульта.
        val settingsButton = ImageButton(this).apply {
            setImageResource(R.drawable.ic_settings)
            val bg = TypedValue()
            context.theme.resolveAttribute(
                android.R.attr.selectableItemBackgroundBorderless, bg, true,
            )
            setBackgroundResource(bg.resourceId)
            val innerPad = (12 * resources.displayMetrics.density).toInt()
            setPadding(innerPad, innerPad, innerPad, innerPad)
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP or Gravity.END,
            )
            setOnClickListener {
                startActivity(Intent(this@MainActivity, SettingsActivity::class.java))
            }
        }

        // FrameLayout — простой контейнер: список на весь экран, статус
        // поверх по центру, кнопка настроек поверх в углу.
        val root = FrameLayout(this).apply {
            addView(
                list,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT,
                    FrameLayout.LayoutParams.MATCH_PARENT,
                ),
            )
            addView(
                statusText,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    Gravity.CENTER,
                ),
            )
            addView(settingsButton)
        }

        setContentView(root)
    }

    // onStart вызывается каждый раз, когда экран становится видимым — в том
    // числе при возврате с экрана настроек. Проверяем актуальный адрес заново,
    // а не только один раз при создании экрана.
    override fun onStart() {
        super.onStart()

        val libraryUrl = Settings.libraryUrl(this)

        // Адрес библиотеки ещё не задан — открываем экран настроек вместо
        // главного экрана. Пользователь не сможет пользоваться приложением,
        // пока не введёт адрес (см. settings/spec.md, сценарий «Первый запуск»).
        //
        // finish() здесь обязателен: без него этот экран остаётся под настройками
        // в стеке, и системная кнопка «назад» из настроек просто вернёт сюда же —
        // onStart снова откроет настройки, и выйти из приложения будет нельзя
        // (особенно важно на ТВ, где «назад» — единственный способ уйти пультом).
        // Убрав себя из стека, мы делаем «назад» из настроек обычным выходом.
        if (libraryUrl.isEmpty()) {
            startActivity(Intent(this, SettingsActivity::class.java))
            finish()
            return
        }

        loadRoot(libraryUrl)
    }

    // Загружает содержимое корня библиотеки: дерево групп (для списка папок
    // верхнего уровня) и каталог файлов (для файлов вне групп). Оба запроса
    // асинхронные, независимые друг от друга — ждём оба, потом строим список.
    private fun loadRoot(libraryUrl: String) {
        showStatus(getString(R.string.library_loading))

        var tree: LibraryTree? = null
        var media: List<MediaCard>? = null
        var failureMessage: String? = null

        // Вызывается после каждого из двух ответов — показывает список, как
        // только оба запроса завершились успешно, либо ошибку, если хоть один
        // из них не удался.
        fun tryRender() {
            val currentTree = tree
            val currentMedia = media
            when {
                failureMessage != null -> showStatus(getString(R.string.library_error, failureMessage))
                currentTree != null && currentMedia != null -> renderRoot(currentTree, currentMedia)
            }
        }

        LibraryApi.getTree(libraryUrl) { result ->
            result.onSuccess { tree = it }
            result.onFailure { failureMessage = it.message ?: it.toString() }
            tryRender()
        }
        LibraryApi.getMedia(libraryUrl) { result ->
            result.onSuccess { media = it }
            result.onFailure { failureMessage = it.message ?: it.toString() }
            tryRender()
        }
    }

    // Строит список корня: группы без родителя и файлы, не состоящие ни в
    // одной группе (их id нет ни в одном membership из дерева). Переход внутрь
    // групп появится на следующем шаге — сейчас список только для чтения.
    private fun renderRoot(tree: LibraryTree, media: List<MediaCard>) {
        val rootGroups = tree.nodes
            .filter { it.parentId == null }
            .sortedBy { it.name }
            .map { LibraryListItem.Group(it) }

        val groupedMediaIds = tree.items.map { it.mediaId }.toHashSet()
        val rootFiles = media
            .filter { it.id !in groupedMediaIds }
            .sortedBy { it.title }
            .map { LibraryListItem.File(it) }

        // Пустая папка (ни групп, ни файлов) получит отдельное состояние на
        // следующем шаге (4.3) — пока просто показываем пустой список.
        val items = rootGroups + rootFiles
        statusText.visibility = View.GONE
        list.visibility = View.VISIBLE
        list.adapter = LibraryListAdapter(items)
    }

    // Показывает текстовое сообщение (загрузка/ошибка/пусто) вместо списка.
    private fun showStatus(message: String) {
        list.visibility = View.GONE
        statusText.visibility = View.VISIBLE
        statusText.text = message
    }
}
